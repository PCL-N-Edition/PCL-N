// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using PCL.Desktop.Platform;
using PCL.Core.Logging;

namespace PCL.Desktop;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const int PipeConnectTimeoutMilliseconds = 1500;
    private readonly string _pipeName;
    private readonly Mutex? _mutex;
    private readonly bool _ownsMutex;
    private CancellationTokenSource? _listenCancellation;
    private Task? _listenTask;
    private int _pendingActivation;
    private bool _disposed;

    private SingleInstanceCoordinator(string pipeName, Mutex? mutex, bool ownsMutex)
    {
        _pipeName = pipeName;
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public event EventHandler? ActivationRequested;

    public bool IsPrimaryInstance => _ownsMutex;

    public static SingleInstanceCoordinator Create()
    {
        string suffix = CreatePerUserSuffix();
        string mutexName = DesktopPlatformApi.CreateSingleInstanceMutexName(suffix);
        string pipeName = $"PCLN.Desktop.{suffix}.activate";

        try
        {
            Mutex mutex = new(initiallyOwned: true, mutexName, out bool createdNew);
            return new SingleInstanceCoordinator(pipeName, mutex, createdNew);
        }
        catch (IOException ex)
        {
            PortableLog.Warn(ex, "SingleInstance", "无法创建命名互斥量，将以主实例模式继续。");
            return new SingleInstanceCoordinator(pipeName, mutex: null, ownsMutex: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            PortableLog.Warn(ex, "SingleInstance", "无权创建命名互斥量，将以主实例模式继续。");
            return new SingleInstanceCoordinator(pipeName, mutex: null, ownsMutex: true);
        }
        catch (PlatformNotSupportedException ex)
        {
            PortableLog.Warn(ex, "SingleInstance", "当前平台不支持命名互斥量，将以主实例模式继续。");
            return new SingleInstanceCoordinator(pipeName, mutex: null, ownsMutex: true);
        }
    }

    public void StartListening()
    {
        if (!IsPrimaryInstance || _listenTask is not null)
            return;

        _listenCancellation = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenAsync(_listenCancellation.Token));
        PortableLog.Info("SingleInstance", "主实例激活管道监听已启动。");
    }

    public int SignalExistingInstance()
    {
        try
        {
            using NamedPipeClientStream pipe = new(".", _pipeName, PipeDirection.Out);
            pipe.Connect(PipeConnectTimeoutMilliseconds);
            pipe.WriteByte(1);
            pipe.Flush();
        }
        catch (IOException ex)
        {
            PortableLog.Warn(ex, "SingleInstance", "连接主实例激活管道失败。");
        }
        catch (TimeoutException ex)
        {
            PortableLog.Warn(ex, "SingleInstance", "连接主实例激活管道超时。");
        }
        catch (UnauthorizedAccessException ex)
        {
            PortableLog.Warn(ex, "SingleInstance", "无权连接主实例激活管道。");
        }

        return 0;
    }

    public bool ConsumePendingActivation() =>
        Interlocked.Exchange(ref _pendingActivation, 0) == 1;

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PortableLog.RealTime("SingleInstance", "等待其他启动器进程发出激活请求。");
            try
            {
                using NamedPipeServerStream pipe = new(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                byte[] buffer = new byte[1];
                _ = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                PortableLog.Info("SingleInstance", "收到其他启动器进程的窗口激活请求。");
                RequestActivation();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException ex)
            {
                PortableLog.Warn(ex, "SingleInstance", "激活管道发生 IO 异常，将重新监听。");
            }
            catch (UnauthorizedAccessException ex)
            {
                PortableLog.Error(ex, "SingleInstance", "激活管道权限不足，监听已停止。");
                return;
            }
        }
    }

    private void RequestActivation()
    {
        Interlocked.Exchange(ref _pendingActivation, 1);
        ActivationRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string CreatePerUserSuffix()
    {
        string identity = string.Join(
            "|",
            Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AppContext.BaseDirectory);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash, 0, 8);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _listenCancellation?.Cancel();
        try
        {
            _listenTask?.Wait(TimeSpan.FromMilliseconds(200));
        }
        catch (AggregateException)
        {
        }
        _listenCancellation?.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }
        _mutex?.Dispose();
    }
}
