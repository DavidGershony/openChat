using System.Net;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Services;

namespace OpenChat.Android.Services.Bridge;

/// <summary>
/// Lightweight HTTP server for the watch bridge.
/// Binds to localhost only (127.0.0.1) so only apps on the same phone can connect.
/// Exposes a REST API for thin clients to read chats/messages and send quick replies.
/// </summary>
public class BridgeHttpService : IDisposable
{
    private readonly ILogger<BridgeHttpService> _logger;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private BridgeAuthService? _authService;
    private BridgeRequestRouter? _router;
    private BridgeSseHandler? _sseHandler;
    private bool _disposed;

    public const int DefaultPort = 18457;
    public const string DefaultPrefix = "http://127.0.0.1:18457/";

    /// <summary>
    /// Whether the bridge is currently running.
    /// </summary>
    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>
    /// The auth service, exposed so the pairing UI can generate codes.
    /// </summary>
    public BridgeAuthService? AuthService => _authService;

    public BridgeHttpService()
    {
        _logger = LoggingConfiguration.CreateLogger<BridgeHttpService>();
    }

    /// <summary>
    /// Start the bridge HTTP server. Call after login when services are available.
    /// </summary>
    public async Task StartAsync(
        IMessageService messageService,
        IStorageService storageService,
        string? currentUserNpub)
    {
        if (IsRunning)
        {
            _logger.LogDebug("Bridge already running, skipping start");
            return;
        }

        _logger.LogInformation("Starting watch bridge on {Prefix}", DefaultPrefix);

        // Create service components
        _authService = new BridgeAuthService(storageService);
        await _authService.LoadPersistedTokenAsync();

        _sseHandler = new BridgeSseHandler(messageService, storageService);
        await _sseHandler.StartAsync();

        _router = new BridgeRequestRouter(messageService, storageService, _authService, _sseHandler);
        _router.SetCurrentUserNpub(currentUserNpub);

        // Start HTTP listener
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add(DefaultPrefix);

        try
        {
            _listener.Start();
            _listenTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _logger.LogInformation("Watch bridge started on {Prefix}", DefaultPrefix);
        }
        catch (HttpListenerException ex)
        {
            _logger.LogError(ex, "Failed to start watch bridge HTTP listener. " +
                "This may indicate HttpListener is not supported on this Android version. " +
                "Bridge functionality will be unavailable.");
            CleanupListener();
        }
    }

    /// <summary>
    /// Stop the bridge HTTP server. Call on logout or app destruction.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            _logger.LogDebug("Bridge not running, skipping stop");
            return;
        }

        _logger.LogInformation("Stopping watch bridge");

        _cts?.Cancel();
        CleanupListener();
        _sseHandler?.Dispose();
        _sseHandler = null;
        _router = null;

        // Keep _authService alive so token persists (only cleared on explicit unpair)

        _logger.LogInformation("Watch bridge stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();

                // Handle CORS preflight
                if (context.Request.HttpMethod == "OPTIONS")
                {
                    context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                    context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    context.Response.Headers.Add("Access-Control-Allow-Headers", "Authorization, Content-Type");
                    context.Response.StatusCode = 204;
                    context.Response.Close();
                    continue;
                }

                // Fire and forget — don't block the accept loop
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_router != null)
                            await _router.HandleRequestAsync(context);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unhandled error in bridge request handler");
                        try
                        {
                            context.Response.StatusCode = 500;
                            context.Response.Close();
                        }
                        catch { }
                    }
                });
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected during shutdown
                break;
            }
            catch (ObjectDisposedException)
            {
                // Listener was disposed
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting bridge connection");
                // Brief pause to avoid tight error loop
                try { await Task.Delay(100, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogDebug("Bridge accept loop exited");
    }

    private void CleanupListener()
    {
        try
        {
            if (_listener?.IsListening == true)
                _listener.Stop();
            _listener?.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error cleaning up HTTP listener: {Error}", ex.Message);
        }
        finally
        {
            _listener = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _cts?.Dispose();
        _logger.LogInformation("Watch bridge disposed");
    }
}
