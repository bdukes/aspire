// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Dcp;

using LogEntry = (int LineNumber, string Content, bool IsErrorMessage);
using LogEntryList = IReadOnlyList<(int LineNumber, string Content, bool IsErrorMessage)>;

internal sealed class ResourceLogSource<TResource>(
    ILogger logger,
    IKubernetesService kubernetesService,
    Version? dcpVersion,
    TResource resource) :
    IAsyncEnumerable<LogEntryList>
    where TResource : CustomResource
{
    public async IAsyncEnumerator<LogEntryList> GetAsyncEnumerator(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            throw new ArgumentException("Cancellation token must be cancellable in order to prevent leaking resources.", nameof(cancellationToken));
        }

        var channel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });

        var streamTasks = new List<Task>();

        var timestamps = resource is Container; // Timestamps are available only for Containers as of Aspire P5.

        if (resource is Container && dcpVersion?.CompareTo(DcpVersion.MinimumVersionAspire_8_1) >= 0)
        {
            var startupStderrStream = await kubernetesService.GetLogStreamAsync(resource, Logs.StreamTypeStartupStdErr, follow: true, timestamps: timestamps, cancellationToken).ConfigureAwait(false);
            var startupStdoutStream = await kubernetesService.GetLogStreamAsync(resource, Logs.StreamTypeStartupStdOut, follow: true, timestamps: timestamps, cancellationToken).ConfigureAwait(false);

            var startupStdoutStreamTask = Task.Run(() => StreamLogsAsync(startupStdoutStream, isError: false), cancellationToken);
            streamTasks.Add(startupStdoutStreamTask);

            var startupStderrStreamTask = Task.Run(() => StreamLogsAsync(startupStderrStream, isError: false), cancellationToken);
            streamTasks.Add(startupStderrStreamTask);
        }

        var stdoutStream = await kubernetesService.GetLogStreamAsync(resource, Logs.StreamTypeStdOut, follow: true, timestamps: timestamps, cancellationToken).ConfigureAwait(false);
        var stderrStream = await kubernetesService.GetLogStreamAsync(resource, Logs.StreamTypeStdErr, follow: true, timestamps: timestamps, cancellationToken).ConfigureAwait(false);

        var stdoutStreamTask = Task.Run(() => StreamLogsAsync(stdoutStream, isError: false), cancellationToken);
        streamTasks.Add(stdoutStreamTask);

        var stderrStreamTask = Task.Run(() => StreamLogsAsync(stderrStream, isError: true), cancellationToken);
        streamTasks.Add(stderrStreamTask);

        // End the enumeration when both streams have been read to completion.
        async Task WaitForStreamsToCompleteAsync()
        {
            await Task.WhenAll(streamTasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            channel.Writer.TryComplete();
        }

        _ = WaitForStreamsToCompleteAsync();
        
        await foreach (var batch in channel.GetBatchesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }

        async Task StreamLogsAsync(Stream stream, bool isError)
        {
            try
            {
                var lineNumber = 1;
                using var sr = new StreamReader(stream, leaveOpen: false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await sr.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        return; // No more data
                    }

                    channel.Writer.TryWriteWithAssert((lineNumber, line, isError));
                    lineNumber++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error happened when capturing logs for {Kind} {Name}", resource.Kind, resource.Metadata.Name);
                channel.Writer.TryComplete(ex);
            }
        }
    }
}
