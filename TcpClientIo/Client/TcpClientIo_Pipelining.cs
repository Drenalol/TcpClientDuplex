using System;
using System.Linq;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Drenalol.Base;
using Microsoft.Extensions.Logging;

namespace Drenalol.Client
{
    public partial class TcpClientIo<TRequest, TResponse>
    {
        private async Task TcpWriteAsync()
        {
            await _semaphore.WaitAsync(_baseCancellationToken);

            try
            {
                while (true)
                {
                    _baseCancellationToken.ThrowIfCancellationRequested();

                    if (!await _bufferBlockRequests.OutputAvailableAsync(_baseCancellationToken))
                        continue;

                    var bytesArray = await _bufferBlockRequests.ReceiveAsync(_baseCancellationToken);
                    await _networkStreamPipeWriter.WriteAsync(bytesArray, _baseCancellationToken);
                    BytesWrite += (ulong) bytesArray.Length;
                    _logger?.LogDebug($"Tcp writed {bytesArray.Length.ToString()} bytes");
                }
            }
            catch (OperationCanceledException canceledException)
            {
                _internalException = canceledException;
            }
            catch (Exception exception)
            {
                _logger?.LogCritical($"{nameof(TcpWriteAsync)} Got {exception.GetType()}, {exception.Message}");
                _internalException = exception;
                throw;
            }
            finally
            {
                StopWriter(_internalException);
                _semaphore.Release();
            }
        }

        private async Task TcpReadAsync()
        {
            await _semaphore.WaitAsync(_baseCancellationToken);

            try
            {
                while (true)
                {
                    _baseCancellationToken.ThrowIfCancellationRequested();
                    var readResult = await _networkStreamPipeReader.ReadAsync(_baseCancellationToken);
                    
                    if (readResult.Buffer.IsEmpty)
                        continue;

                    foreach (var buffer in readResult.Buffer)
                    {
                        await _deserializePipeWriter.WriteAsync(buffer, _baseCancellationToken);
                        BytesRead += (ulong) buffer.Length;
                        _logger?.LogDebug($"Tcp readed {buffer.Length.ToString()} bytes");
                    }
                    
                    _networkStreamPipeReader.AdvanceTo(readResult.Buffer.End);
                }
            }
            catch (OperationCanceledException canceledException)
            {
                _internalException = canceledException;
            }
            catch (Exception exception)
            {
                _logger?.LogCritical($"{nameof(TcpReadAsync)} Got {exception.GetType()}, {exception}");
                _internalException = exception;
                throw;
            }
            finally
            {
                StopReader(_internalException);
                _semaphore.Release();
            }
        }

        private async Task DeserializeResponseAsync()
        {
            try
            {
                while (true)
                {
                    _baseCancellationToken.ThrowIfCancellationRequested();
                    var (responseId, responseLength, response) = await _serializer.DeserializeAsync(_deserializePipeReader, _baseCancellationToken);
                    _logger?.LogDebug($"Deserialized response: Id {responseId} Length {responseLength.ToString()} bytes");
                    await SetResponseAsync(responseId, response);
                }
            }
            catch (OperationCanceledException canceledException)
            {
                _internalException = canceledException;
            }
            catch (Exception exception)
            {
                _logger?.LogCritical($"{nameof(DeserializeResponseAsync)} Got {exception.GetType()}, {exception}");
                _internalException = exception;
                throw;
            }
            finally
            {
                StopDeserializeWriterReader(_internalException);
            }
        }

        private void StopDeserializeWriterReader(Exception exception)
        {
            _logger?.LogDebug("Completion Deserializer PipeWriter and PipeReader started");
            _deserializePipeWriter.CancelPendingFlush();
            _deserializePipeReader.CancelPendingRead();

            if (_tcpClient.Client.Connected)
            {
                _deserializePipeWriter.Complete(exception);
                _deserializePipeReader.Complete(exception);
            }

            _logger?.LogDebug("Completion Deserializer PipeWriter and PipeReader ended");
        }

        private void StopReader(Exception exception)
        {
            _logger?.LogDebug("Completion NetworkStream PipeReader started");

            if (_disposing)
            {
                foreach (var completedResponse in _completeResponses.Where(tcs => tcs.Value.Task.Status == TaskStatus.WaitingForActivation))
                {
                    var innerException = exception ?? new OperationCanceledException();
                    _logger?.LogDebug($"Set force {innerException.GetType()} in {nameof(TaskCompletionSource<ITcpBatch<TResponse>>)} in {nameof(TaskStatus.WaitingForActivation)}");
                    completedResponse.Value.TrySetException(innerException);
                }
            }

            _networkStreamPipeReader.CancelPendingRead();

            if (_tcpClient.Client.Connected)
                _networkStreamPipeReader.Complete(exception);

            _logger?.LogDebug("Completion NetworkStream PipeReader ended");
        }

        private void StopWriter(Exception exception)
        {
            _logger?.LogDebug("Completion NetworkStream PipeWriter started");
            _networkStreamPipeWriter.CancelPendingFlush();

            if (_tcpClient.Client.Connected)
                _networkStreamPipeWriter.Complete(exception);

            _logger?.LogDebug("Completion NetworkStream PipeWriter ended");
        }
    }
}