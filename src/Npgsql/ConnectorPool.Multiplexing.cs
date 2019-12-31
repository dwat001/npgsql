using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Npgsql.TypeMapping;
using Npgsql.Util;
using static Npgsql.Util.Statics;

namespace Npgsql
{
    sealed partial class ConnectorPool
    {
        readonly ChannelReader<NpgsqlCommand>? _multiplexCommandReader;
        internal ChannelWriter<NpgsqlCommand>? MultiplexCommandWriter { get; }

        const int WriteCoalescineDelayAdaptivityUs = 10;

        /// <summary>
        /// A pool-wide type mapper used when multiplexing. This is necessary because binding parameters
        /// to their type handlers happens *before* the command is enqueued for execution, so there's no
        /// connector yet at that stage.
        /// </summary>
        internal ConnectorTypeMapper? MultiplexingTypeMapper { get; private set; }

        /// <summary>
        /// When multiplexing is enabled, determines the maximum amount of time to wait for further
        /// commands before flushing to the network. In ticks (100ns), 0 disables waiting.
        /// This is in 100ns ticks, not <see cref="Stopwatch"/> ticks whose meaning vary across platforms.
        /// </summary>
        readonly long _writeCoalescingDelayTicks;

        /// <summary>
        /// When multiplexing is enabled, determines the maximum number of outgoing bytes to buffer before
        /// flushing to the network.
        /// </summary>
        readonly int _writeCoalescingBufferThresholdBytes;

        internal long NumCommandsSent;
        internal long NumBatches;
        long _ticksWritten;
        long _waitsForFurtherCommands;
        // long _bytesFlushed;

        /// <summary>
        /// Called exactly once per multiplexing pool, when the first connection is opened, with two goals:
        /// 1. Load types and bind the pool-wide type mapper (necessary for binding parameters)
        /// 2. Cause any connection exceptions (e.g. bad username) to be thrown from NpgsqlConnection.Open
        /// </summary>
        internal async Task BootstrapMultiplexing(NpgsqlConnection conn, NpgsqlTimeout timeout, bool async, CancellationToken cancellationToken = default)
        {
            Debug.Assert(_multiplexing);

            var connector =
                await conn.StartBindingScope(ConnectorBindingScope.Connection, timeout, async, cancellationToken);
            using var _ = Defer(() => conn.EndBindingScope(ConnectorBindingScope.Connection));

            // Somewhat hacky. Extract the connector's type mapper as our pool-wide mapper,
            // and have the connector rebind to ensure it has a different instance.
            // The latter isn't strictly necessary (type mappers should always be usable
            // concurrently) but just in case.
            MultiplexingTypeMapper = connector.TypeMapper;
            connector.RebindTypeMapper();

            IsBootstrapped = true;
        }

        async Task MultiplexingWriteLoopWrapper()
        {
            try
            {
                await MultiplexingWriteLoop();
            }
            catch (Exception e)
            {
                Log.Error("Exception in multiplexing write loop, this is an Npgsql bug, please file an issue.", e);
            }
        }

        async Task MultiplexingWriteLoop()
        {
            // This method is async, but only ever yields when there are no pending commands in the command channel.
            // No I/O should ever be performed asynchronously, as that would block further writing for the entire
            // application; whenever I/O cannot complete immediately, we chain a callback with ContinueWith and move
            // on to the next connector.
            Debug.Assert(_multiplexCommandReader != null);

            var timeout = _writeCoalescingDelayTicks / 2;
            var timeoutTokenSource = new CancellationTokenSource();
            var timeoutToken = timeout == 0 ? CancellationToken.None : timeoutTokenSource.Token;

            // TODO: Writing I/O here is currently async-only. Experiment with both sync and async (based on user
            // preference, ExecuteReader vs. ExecuteReaderAsync).
            while (true)
            {
                var stats = new MultiplexingStats { Stopwatch = new Stopwatch() };
                NpgsqlConnector? connector;

                // Get a first command out.
                if (!_multiplexCommandReader.TryRead(out var command))
                    command = await _multiplexCommandReader.ReadAsync();

                try
                {
                    // First step is to get a connector on which to execute
                    var spinwait = new SpinWait();
                    while (true)
                    {
                        if (TryGetIdleConnector(out connector))
                        {
                            // See increment under over-capacity mode below
                            Interlocked.Increment(ref connector.CommandsInFlightCount);
                            break;
                        }

                        connector = await OpenNewConnector(
                            command.Connection!,
                            new NpgsqlTimeout(TimeSpan.FromSeconds(Settings.Timeout)),
                            async: true,
                            CancellationToken.None);

                        if (connector != null)
                        {
                            // Managed to created a new connector
                            connector.Connection = null;

                            // See increment under over-capacity mode below
                            Interlocked.Increment(ref connector.CommandsInFlightCount);

                            break;
                        }

                        // There were no idle connectors and we're at max capacity, so we can't open a new one.
                        // Enter over-capacity mode - find an unlocked connector with the least currently in-flight
                        // commands and sent on it, even though there are already pending commands.
                        var minInFlight = int.MaxValue;
                        foreach (var c in _connectors)
                        {
                            if (c?.MultiplexAsyncWritingLock == 0 && c.CommandsInFlightCount < minInFlight)
                            {
                                minInFlight = c.CommandsInFlightCount;
                                connector = c;
                            }
                        }

                        // There could be no writable connectors (all stuck in transaction or flushing).
                        if (connector == null)
                        {
                            // TODO: This is problematic - when absolutely all connectors are both busy *and* currently
                            // performing (async) I/O, this will spin-wait.
                            // We could call WaitAsync, but that would wait for an idle connector, whereas we want any
                            // writeable (non-writing) connector even if it has in-flight commands. Maybe something
                            // with better back-off.
                            // On the other hand, this is exactly *one* thread doing spin-wait, maybe not that bad.
                            spinwait.SpinOnce();
                            continue;
                        }

                        // We may be in a race condition with the connector read loop, which may be currently returning
                        // the connector to the Idle channel (because it has completed all commands).
                        // Increment the in-flight count to make sure the connector isn't returned as idle.
                        var newInFlight = Interlocked.Increment(ref connector.CommandsInFlightCount);
                        if (newInFlight == 1)
                        {
                            // The connector's in-flight was 0, so it was idle - abort over-capacity read
                            // and retry the normal flow.
                            Interlocked.Decrement(ref connector.CommandsInFlightCount);
                            spinwait.SpinOnce();
                            continue;
                        }

                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Exception opening a connection", ex);

                    // Fail the first command in the channel as a way of bubbling the exception up to the user
                    command.ExecutionCompletion.SetException(ex);

                    continue;
                }

                // We now have a ready connector, and can start writing commands to it.
                Debug.Assert(connector != null);

                try
                {
                    stats.Reset();

                    // Read queued commands and write them to the connector's buffer, for as long as we're
                    // under our write threshold and timer delay.
                    // Note we already have one command we read above, and have already updated the connector's
                    // CommandsInFlightCount. Now write that command.
                    var writtenSynchronously = WriteCommand(connector, command, ref stats);

                    if (timeout == 0)
                    {
                        while (connector.WriteBuffer.WritePosition < _writeCoalescingBufferThresholdBytes &&
                               writtenSynchronously &&
                               _multiplexCommandReader.TryRead(out command))
                        {
                            Interlocked.Increment(ref connector.CommandsInFlightCount);
                            writtenSynchronously = WriteCommand(connector, command, ref stats);
                        }
                    }
                    else
                    {
                        // We reuse the timeout's cancellation token source as long as it hasn't fired, but once it has
                        // there's no way to reset it (see https://github.com/dotnet/runtime/issues/4694)
                        var timeoutTimeSpan = TimeSpan.FromTicks(timeout);
                        timeoutTokenSource.CancelAfter(timeoutTimeSpan);
                        if (timeoutTokenSource.IsCancellationRequested)
                        {
                            timeoutTokenSource.Dispose();
                            timeoutTokenSource = new CancellationTokenSource(timeoutTimeSpan);
                            timeoutToken = timeoutTokenSource.Token;
                        }

                        try
                        {
                            while (connector.WriteBuffer.WritePosition < _writeCoalescingBufferThresholdBytes &&
                                   writtenSynchronously)
                            {
                                if (!_multiplexCommandReader.TryRead(out command))
                                {
                                    _waitsForFurtherCommands++;
                                    command = await _multiplexCommandReader.ReadAsync(timeoutToken);
                                }

                                Interlocked.Increment(ref connector.CommandsInFlightCount);
                                writtenSynchronously = WriteCommand(connector, command, ref stats);
                            }

                            // The cancellation token (presumably!) has not fired, reset its timer so
                            // we can reuse the cancellation token source instead of reallocating
                            timeoutTokenSource.CancelAfter(int.MaxValue);

                            // Increase the timeout slightly for next time: we're under load, so allow more
                            // commands to get coalesced into the same packet (up to the hard limit)
                            timeout = Math.Min(timeout + WriteCoalescineDelayAdaptivityUs, _writeCoalescingDelayTicks);
                        }
                        catch (OperationCanceledException)
                        {
                            // Timeout fired, we're done writing.
                            // Reduce the timeout slightly for next time: we're under little load, so reduce impact
                            // on latency
                            timeout = Math.Max(timeout - WriteCoalescineDelayAdaptivityUs, 0);
                        }
                    }

                    // If all commands were written synchronously (good path), complete the write here, flushing
                    // and updating statistics. If not, CompleteRewrite is scheduled to run later, when the async
                    // operations complete, so skip it and continue.
                    if (writtenSynchronously)
                        CompleteWrite(connector, ref stats);
                }
                catch (Exception ex)
                {
                    FailWrite(connector, ex);
                }
            }

            bool WriteCommand(NpgsqlConnector connector, NpgsqlCommand command, ref MultiplexingStats stats)
            {
                // Note: this method *never* awaits on I/O - doing so would suspend all outgoing multiplexing commands
                // for the entire pool. In the normal/fast case, writing the command is purely synchronous (serialize
                // to buffer in memory), and the actual flush will occur at the level above. For cases where the
                // command overflows the buffer, async I/O is done, and we schedule continuations separately -
                // but the main thread continues to handle other commands on other connectors.
                // TODO: Need to flow the behavior (SchemaOnly support etc.), cancellation token, async-ness (?)...
                if (_autoPrepare)
                {
                    var numPrepared = 0;
                    foreach (var statement in command._statements)
                    {
                        // If this statement isn't prepared, see if it gets implicitly prepared.
                        // Note that this may return null (not enough usages for automatic preparation).
                        if (!statement.IsPrepared)
                            statement.PreparedStatement = connector.PreparedStatementManager.TryGetAutoPrepared(statement);
                        if (statement.PreparedStatement is PreparedStatement pStatement)
                        {
                            numPrepared++;
                            if (pStatement?.State == PreparedState.NotPrepared)
                            {
                                pStatement.State = PreparedState.BeingPrepared;
                                statement.IsPreparing = true;
                            }
                        }
                    }
                }

                var written = connector.CommandsInFlightWriter!.TryWrite(command);
                Debug.Assert(written, $"Failed to enqueue command to {connector.CommandsInFlightWriter}");

                // Purposefully don't wait for I/O to complete
                var task = command.Write(connector, async: true);
                stats.NumCommands++;

                switch (task.Status)
                {
                case TaskStatus.RanToCompletion:
                    return true;

                case TaskStatus.Faulted:
                    task.GetAwaiter().GetResult(); // Throw the exception
                    return true;

                case TaskStatus.Running:
                {
                    // Asynchronous completion, which means the writing is flushing to network and there's actual I/O
                    // (i.e. a big command which overflowed our buffer).
                    // We don't (ever) await in the write loop, so remove the connector from the writable list (as it's
                    // still flushing) and schedule a continuation to continue taking care of this connector.
                    // The write loop continues to the next connector.
                    connector.FlagAsNotWritableForMultiplexing();

                    // Create a copy of the statistics and purposefully box it via the closure. We need a separate
                    // copy of the stats for the async writing that will continue in parallel with this loop.
                    var clonedStats = stats;

                    // ReSharper disable once MethodSupportsCancellation
                    task.ContinueWith((t, o) =>
                    {
                        var conn = (NpgsqlConnector)o!;

                        if (t.IsFaulted)
                        {
                            FailWrite(conn, t.Exception!.UnwrapAggregate());
                            return;
                        }

                        // There's almost certainly more buffered outgoing data for the command, after the flush
                        // occured. Complete the write, which will flush again (and update statistics).
                        try
                        {
                            // TODO: When we do statistics, everything needs to be captured in this closure - to
                            // create a copy, otherwise the next synchronous iteration will reuse the same variables.
                            CompleteWrite(conn, ref clonedStats);
                        }
                        catch (Exception e)
                        {
                            FailWrite(conn, e);
                        }
                    }, connector);

                    return false;
                }

                default:
                    throw new Exception("When writing command to connector, task is in invalid state " + task.Status);
                }
            }

            void CompleteWrite(NpgsqlConnector connector, ref MultiplexingStats stats)
            {
                var task = connector.Flush(async: true);
                switch (task.Status)
                {
                case TaskStatus.RanToCompletion:
                    UpdateStatistics(ref stats);
                    return;

                case TaskStatus.Faulted:
                    task.GetAwaiter().GetResult(); // Throw the exception
                    return;

                case TaskStatus.Running:
                {
                    // Asynchronous completion - the flush didn't complete immediately (e.g. TCP zero window).
                    connector.FlagAsNotWritableForMultiplexing();

                    // Create a copy of the statistics and purposefully box it via the closure. We need a separate
                    // copy of the stats for the async writing that will continue in parallel with this loop.
                    var clonedStats = stats;

                    task.ContinueWith((t, o) =>
                    {
                        var c = (NpgsqlConnector)o!;
                        if (t.IsFaulted)
                        {
                            FailWrite(c, t.Exception!.UnwrapAggregate());
                            return;
                        }

                        // Flushing has completed, it's safe to write to this connector again
                        c.FlagAsWritableForMultiplexing();

                        UpdateStatistics(ref clonedStats);
                    }, connector);

                    return;
                }

                default:
                    throw new Exception("When flushing, task is in invalid state " + task.Status);
                }

                void UpdateStatistics(ref MultiplexingStats stats)
                {
                    // TODO: The following is very temporary - I already have proper perf counter support
                    // standing by for some upstream changes in the MS perf lab.

                    Interlocked.Add(ref NumCommandsSent, stats.NumCommands);
                    _ticksWritten += stats.Stopwatch!.ElapsedTicks;
                    // TODO: Multiple writes/flushes may occur while writing big commands, so there needs to be a
                    // feature in NpgsqlWriteBuffer to reset the counter, like a Stopwatch.
                    // _bytesFlushed += connector.WriteBuffer.WritePosition;
                    // TODO: Same for flushes, except if we're interested only in flushes happening directly by the
                    // write loop.
                    var numFlushes = Interlocked.Increment(ref NumBatches);
                    // if (numFlushes % 100000 == 0)
                    // {
                    //     Console.WriteLine(
                    //         $"Commands: Average commands per batch: {(double)NumCommandsSent / NumBatches} " +
                    //         $"({NumCommandsSent}/{NumBatches})");
                    //     Console.WriteLine($"Total physical connections: {_numConnectors}");
                    //     Console.WriteLine($"Average batch time (us): {_ticksWritten / NumBatches / 1000}");
                    //     // Console.WriteLine($"Average write buffer position: {_bytesFlushed / NumFlushes}");
                    //     Console.WriteLine(
                    //         $"Average waits for further commands: {(double)_waitsForFurtherCommands / NumBatches}");
                    // }
                }
            }

            void FailWrite(NpgsqlConnector connector, Exception exception)
            {
                // Note that all commands already passed validation before being enqueued. This means any error
                // here is either an unrecoverable network issue (in which case we're already broken), or some other
                // issue while writing (e.g. invalid UTF8 characters in the SQL query) - unrecoverable in any case.

                // All commands enqueued in CommandsInFlightWriter will be drained by the reader and failed.
                // Note that some of these commands where only written to the connector's buffer, but never
                // actually sent - because of a later exception.
                // In theory, we could track commands that were only enqueued and not sent, and retry those
                // (on another connector), but that would add some book-keeping and complexity, and in any case
                // if one connector was broken, chances are that all are (networking).
                Debug.Assert(connector.IsBroken);

                Log.Error("Exception while writing commands", exception, connector.Id);
            }

            // ReSharper disable once FunctionNeverReturns
        }

        struct MultiplexingStats
        {
            internal Stopwatch Stopwatch;
            internal int NumCommands;

            internal void Reset()
            {
                Stopwatch.Restart();
                NumCommands = 0;
            }
        }
    }
}
