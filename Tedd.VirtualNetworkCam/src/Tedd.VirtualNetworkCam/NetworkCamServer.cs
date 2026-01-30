﻿using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KinectCam;

namespace Tedd.VirtualNetworkCam
{
    internal class NetworkCamServer
    {
        private readonly int _port;
        private readonly VirtualCamFilter _camFilter;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly List<NetworkCamServerClient> _clients = new List<NetworkCamServerClient>();

        public NetworkCamServer(int port, VirtualCamFilter camFilter)
        {
            _port = port;
            _camFilter = camFilter;
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public async Task StartAsync()
        {
            Logger.Info("Is64BitProcess: " + Environment.Is64BitProcess);
            Logger.Info("Listening to TCP port " + _port);

            var listener = new TcpListener(_port);
            listener.Start();

            _cancellationTokenSource.Token.Register(listener.Stop);

            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Logger.Info("Accepting new client: " + client.Client.RemoteEndPoint);

                    _ = HandleClient(client, _cancellationTokenSource.Token);
                }
                catch (ObjectDisposedException) when (_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    Logger.Info("TcpListener stopped listening because cancellation was requested.");
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error handling client.");
                }
            }
        }

        public Task StopAsync()
        {
            _cancellationTokenSource.Cancel();
            return Task.CompletedTask;
        }

        private Task HandleClient(TcpClient client, CancellationToken token)
        {
            var c = new NetworkCamServerClient(client, token, _camFilter);

            // Add reference
            lock (_clients)
                _clients.Add(c);

            // Remove reference
            c.Closed += cc =>
            {
                lock (_clients)
                    _clients.Remove(cc);
            };

            return Task.CompletedTask;
        }
    }
}

