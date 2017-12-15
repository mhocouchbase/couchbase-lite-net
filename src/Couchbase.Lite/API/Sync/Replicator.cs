﻿// 
//  Replicator.cs
// 
//  Author:
//   Jim Borden  <jim.borden@couchbase.com>
// 
//  Copyright (c) 2017 Couchbase, Inc All rights reserved.
// 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
// 
//  http://www.apache.org/licenses/LICENSE-2.0
// 
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
// 

using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Couchbase.Lite.DI;
using Couchbase.Lite.Logging;
using Couchbase.Lite.Support;
using Couchbase.Lite.Util;

using JetBrains.Annotations;

using LiteCore;
using LiteCore.Interop;
using LiteCore.Util;

using Microsoft.Extensions.DependencyInjection;

namespace Couchbase.Lite.Sync
{
    /// <summary>
    /// An object that is responsible for the replication of data between two
    /// endpoints.  The replication can set up to be pull only, push only, or both
    /// (i.e. pusher and puller are no longer separate) between a database and a URL
    /// or a database and another database on the same filesystem.
    /// </summary>
    public sealed unsafe class Replicator : IDisposable
    {
        #region Constants

        private const int MaxOneShotRetryCount = 2;

        private const string Tag = nameof(Replicator);
        private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(10);

        [NotNull]
        private static readonly C4ReplicatorMode[] Modes = {
            C4ReplicatorMode.Disabled, C4ReplicatorMode.Disabled, C4ReplicatorMode.OneShot, C4ReplicatorMode.Continuous
        };

        #endregion

        #region Variables

        [NotNull]
        private readonly ReplicatorConfiguration _config;

        [NotNull]
        private readonly ThreadSafety _databaseThreadSafety;

        [NotNull]
        private readonly Event<ReplicationStatusChangedEventArgs> _statusChanged =
            new Event<ReplicationStatusChangedEventArgs>();

        [NotNull]
        private readonly SerialQueue _threadSafetyQueue = new SerialQueue();

        private string _desc;
        private bool _disposed;

        private ReplicatorParameters _nativeParams;
        private C4ReplicatorStatus _rawStatus;
        private IReachability _reachability;
        private C4Replicator* _repl;
        private int _retryCount;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the configuration that was used to create this Replicator
        /// </summary>
        [NotNull]
        public ReplicatorConfiguration Config => ReplicatorConfiguration.Clone(_config);

        /// <summary>
        /// Gets the current status of the <see cref="Replicator"/>
        /// </summary>
        public ReplicationStatus Status { get; set; }

        #endregion

        #region Constructors

        static Replicator()
        {
            WebSocketTransport.RegisterWithC4();
        }

        /// <summary>
        /// Constructs a replicator based on the given <see cref="ReplicatorConfiguration"/>
        /// </summary>
        /// <param name="config">The configuration to use to create the replicator</param>
        public Replicator([NotNull]ReplicatorConfiguration config)
        {
            _config = CBDebug.MustNotBeNull(Log.To.Sync, Tag, nameof(config), ReplicatorConfiguration.Clone(config));
            _databaseThreadSafety = _config.Database.ThreadSafety;
        }

        /// <summary>
        /// Finalizer
        /// </summary>
        ~Replicator()
        {
            Dispose(true);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Adds a change listener on this replication object (similar to a C# event)
        /// </summary>
        /// <param name="handler">The logic to run during the callback</param>
        /// <returns>A token to remove the handler later</returns>
        [ContractAnnotation("null => halt")]
        public ListenerToken AddChangeListener(EventHandler<ReplicationStatusChangedEventArgs> handler)
        {
            CBDebug.MustNotBeNull(Log.To.Sync, Tag, nameof(handler), handler);

            return AddChangeListener(null, handler);
        }

        /// <summary>
        /// Adds a change listener on this replication object (similar to a C# event, but
        /// with the ability to specify a <see cref="TaskScheduler"/> to schedule the 
        /// handler to run on)
        /// </summary>
        /// <param name="scheduler">The <see cref="TaskScheduler"/> to run the <c>handler</c> on
        /// (<c>null</c> for default)</param>
        /// <param name="handler">The logic to run during the callback</param>
        /// <returns>A token to remove the handler later</returns>
        [ContractAnnotation("handler:null => halt")]
        public ListenerToken AddChangeListener([CanBeNull]TaskScheduler scheduler,
            EventHandler<ReplicationStatusChangedEventArgs> handler)
        {
            CBDebug.MustNotBeNull(Log.To.Sync, Tag, nameof(handler), handler);

            var cbHandler = new CouchbaseEventHandler<ReplicationStatusChangedEventArgs>(handler, scheduler);
            _statusChanged.Add(cbHandler);
            return new ListenerToken(cbHandler, "repl");
        }

        /// <summary>
        /// Removes a previously added change listener via its <see cref="ListenerToken"/>
        /// </summary>
        /// <param name="token">The token received from <see cref="AddChangeListener(TaskScheduler, EventHandler{ReplicationStatusChangedEventArgs})"/></param>
        public void RemoveChangeListener(ListenerToken token)
        {
            _statusChanged.Remove(token);
        }

        /// <summary>
        /// Starts the replication
        /// </summary>
        public void Start()
        {
            _threadSafetyQueue.DispatchSync(() =>
            {
                if (_disposed) {
                    throw new ObjectDisposedException("Replication cannot be started after disposal");
                }

                if (_repl != null) {
                    Log.To.Sync.W(Tag, $"{this} has already started");
                    return;
                }

                Log.To.Sync.I(Tag, $"{this}: Starting");
                _retryCount = 0;
                StartInternal();
            });
        }

        /// <summary>
        /// Stops the replication
        /// </summary>
        public void Stop()
        {
            _threadSafetyQueue.DispatchSync(() =>
            {
                _reachability?.Stop();
                _reachability = null;
                if (_repl != null) {
                    Native.c4repl_stop(_repl);
                }
            });
        }

        #endregion

        #region Private Methods

        private static C4ReplicatorMode Mkmode(bool active, bool continuous)
        {
            return Modes[2 * Convert.ToInt32(active) + Convert.ToInt32(continuous)];
        }

        private static void OnDocError(bool pushing, string docID, C4Error error, bool transient, object context)
        {
            var replicator = context as Replicator;
            replicator?._threadSafetyQueue.DispatchAsync(() =>
            {
                replicator.OnDocError(error, pushing, docID, transient);
            });
        }

        private static TimeSpan RetryDelay(int retryCount)
        {
            var delaySecs = 1 << Math.Min(retryCount, 30);
            return TimeSpan.FromSeconds(Math.Min(delaySecs, MaxRetryDelay.TotalSeconds));
        }

        private static void StatusChangedCallback(C4ReplicatorStatus status, object context)
        {
            //TODO: Change to async
            var repl = context as Replicator;
            repl?._threadSafetyQueue.DispatchSync(() =>
            {
                repl.StatusChangedCallback(status);
            });
        }

        private static bool ValidateCallback(string docID, IntPtr body, object context)
        {
            return true;
        }

        private void ClearRepl()
        {
            _threadSafetyQueue.DispatchSync(() =>
            {
                if (_disposed) {
                    return;
                }

                Native.c4repl_free(_repl);
                _repl = null;
                _desc = null;
            });
        }

        private void Dispose(bool finalizing)
        {
            _threadSafetyQueue.DispatchSync(() =>
            {
                if (_disposed) {
                    return;
                }

                if (!finalizing) {
                    _nativeParams?.Dispose();
                    if (Status.Activity != ReplicatorActivityLevel.Stopped) {
                        var newStatus = new ReplicationStatus(ReplicatorActivityLevel.Stopped, Status.Progress, null);
                        _statusChanged.Fire(this, new ReplicationStatusChangedEventArgs(newStatus));
                        Status = newStatus;
                    }
                }

                Native.c4repl_free(_repl);
                _repl = null;
                _disposed = true;
            });
        }

        private bool HandleError(C4Error error)
        {
            // If this is a transient error, or if I'm continuous and the error might go away with a change
            // in network (i.e. network down, hostname unknown), then go offline and retry later
            var transient = Native.c4error_mayBeTransient(error);
            if (!transient && !(_config.Continuous && Native.c4error_mayBeNetworkDependent(error))) {
                return false; // Nope, this is permanent
            }

            if (!_config.Continuous && _retryCount >= MaxOneShotRetryCount) {
                return false; //Too many retries
            }

            ClearRepl();
            if (transient) {
                // On transient error, retry periodically, with exponential backoff
                var delay = RetryDelay(++_retryCount);
                Log.To.Sync.I(Tag,
                    $"{this}: Transient error ({Native.c4error_getMessage(error)}); will retry in {delay}...");
                _threadSafetyQueue.DispatchAfter(Retry, delay);
            } else {
                Log.To.Sync.I(Tag,
                    $"{this}: Network error ({Native.c4error_getMessage(error)}); will retry when network changes...");
            }

            // Also retry when the network changes
            StartReachabilityObserver();
            return true;
        }

        // Must be called from within the SerialQueue
        private void OnDocError(C4Error error, bool pushing, [NotNull]string docID, bool transient)
        {
            var logDocID = new SecureLogString(docID, LogMessageSensitivity.PotentiallyInsecure);
            if (!pushing && error.domain == C4ErrorDomain.LiteCoreDomain && error.code == (int) C4ErrorCode.Conflict) {
                // Conflict pulling a document -- the revision was added but app needs to resolve it:
                var safeDocID = new SecureLogString(docID, LogMessageSensitivity.PotentiallyInsecure);
                Log.To.Sync.I(Tag, $"{this} pulled conflicting version of '{safeDocID}'");
                try {
                    _config.Database.ResolveConflict(docID, Config.ConflictResolver);
                } catch (Exception e) {
                    Log.To.Sync.W(Tag, $"Conflict resolution of '{logDocID}' failed", e);
                }
            } else {
                var transientStr = transient ? "transient " : String.Empty;
                var dirStr = pushing ? "pushing" : "pulling";
                Log.To.Sync.I(Tag,
                    $"{this}: {transientStr}error {dirStr} '{logDocID}' : {error.code} ({Native.c4error_getMessage(error)})");
            }
        }

        private void ReachabilityChanged(object sender, NetworkReachabilityChangeEventArgs e)
        {
            Debug.Assert(e != null);

            _threadSafetyQueue.DispatchAsync(() =>
            {
                if (_repl == null && e.Status == NetworkReachabilityStatus.Reachable) {
                    Log.To.Sync.I(Tag, $"{this}: Server may now be reachable; retrying...");
                    _retryCount = 0;
                    Retry();
                }
            });
        }

        // Must be called from within the SerialQueue
        private void Retry()
        {
            if (_repl != null || _rawStatus.level != C4ReplicatorActivityLevel.Offline) {
                return;
            }

            Log.To.Sync.I(Tag, $"{this}: Retrying...");
            StartInternal();
        }

        // Must be called from within the SerialQueue
        private void StartInternal()
        {
            _desc = ToString(); // Cache this; it may be called a lot when logging

            // Target:
            var addr = new C4Address();
            var scheme = new C4String();
            var host = new C4String();
            var path = new C4String();
            Database otherDB = null;
            var remoteUrl = _config.RemoteUrl;
            string dbNameStr = null;
            if (remoteUrl != null) {
                var pathStr = String.Concat(remoteUrl.Segments.Take(remoteUrl.Segments.Length - 1));
                dbNameStr = remoteUrl.Segments.Last().TrimEnd('/');
                scheme = new C4String(remoteUrl.Scheme);
                host = new C4String(remoteUrl.Host);
                path = new C4String(pathStr);
                addr.scheme = scheme.AsC4Slice();
                addr.hostname = host.AsC4Slice();
                addr.port = (ushort) remoteUrl.Port;
                addr.path = path.AsC4Slice();
            } else {
                otherDB = _config.OtherDB;
            }

            var options = _config.Options;
            var userInfo = remoteUrl?.UserInfo?.Split(':');
            if (userInfo?.Length == 2 && options.Auth == null) {
                _config.Authenticator = new BasicAuthenticator(userInfo[0], userInfo[1]);
            }

            _config.Authenticator?.Authenticate(_config.Options);

            options.Freeze();
            var push = _config.ReplicatorType.HasFlag(ReplicatorType.Push);
            var pull = _config.ReplicatorType.HasFlag(ReplicatorType.Pull);
            var continuous = _config.Continuous;
            _nativeParams = new ReplicatorParameters(Mkmode(push, continuous), Mkmode(pull, continuous), options, ValidateCallback, 
                OnDocError, StatusChangedCallback, this);


            var err = new C4Error();
            var status = default(C4ReplicatorStatus);
            _databaseThreadSafety.DoLocked(() =>
            {
                C4Error localErr;
                _repl = Native.c4repl_new(_config.Database.c4db, addr, dbNameStr, otherDB != null ? otherDB.c4db : null,
                    _nativeParams.C4Params, &localErr);
                err = localErr;
                if (_repl != null) {
                    status = Native.c4repl_getStatus(_repl);
                    _config.Database.ActiveReplications.Add(this);
                } else {
                    status = new C4ReplicatorStatus {
                        error = err,
                        level = C4ReplicatorActivityLevel.Stopped,
                        progress = new C4Progress()
                    };
                }
            });

            scheme.Dispose();
            path.Dispose();
            host.Dispose();

            UpdateStateProperties(status);
            StatusChangedCallback(status, this);

        }

        private void StartReachabilityObserver()
        {
            if (_reachability != null) {
                return;   
            }

            _reachability = Service.Provider.GetService<IReachability>() ?? new Reachability();
            _reachability.StatusChanged += ReachabilityChanged;
            _reachability.Start();
        }

        // Must be called from within the SerialQueue
        private void StatusChangedCallback(C4ReplicatorStatus status)
        {
            if (status.level == C4ReplicatorActivityLevel.Stopped) {
                if (HandleError(status.error)) {
                    status.level = C4ReplicatorActivityLevel.Offline;
                }
            } else if (status.level > C4ReplicatorActivityLevel.Connecting) {
                _retryCount = 0;
                _reachability?.Stop();
                _reachability = null;
            }

            UpdateStateProperties(status);
            if (status.level == C4ReplicatorActivityLevel.Stopped) {
                ClearRepl();
                _config.Database.ActiveReplications.Remove(this);
            }

            try {
                _statusChanged.Fire(this, new ReplicationStatusChangedEventArgs(Status));
            } catch (Exception e) {
                Log.To.Sync.W(Tag, "Exception during StatusChanged callback", e);
            }
        }

        private void UpdateStateProperties(C4ReplicatorStatus state)
        {
            Exception error = null;
            if (state.error.code > 0) {
                error = new LiteCoreException(state.error);
            }

            _rawStatus = state;

            var level = (ReplicatorActivityLevel) state.level;
            var progress = new ReplicationProgress(state.progress.unitsCompleted, state.progress.unitsTotal);
            Status = new ReplicationStatus(level, progress, error);
            Log.To.Sync.I(Tag, $"{this} is {state.level}, progress {state.progress.unitsCompleted}/{state.progress.unitsTotal}");
        }

        #endregion

        #region Overrides

        /// <inheritdoc />
        public override string ToString()
        {
            if (_desc != null) {
                return _desc;
            }

            var sb = new StringBuilder(3, 3);
            if (_config.ReplicatorType.HasFlag(ReplicatorType.Pull)) {
                sb.Append("<");
            }

            if (_config.Continuous) {
                sb.Append("*");
            }

            if (_config.ReplicatorType.HasFlag(ReplicatorType.Push)) {
                sb.Append(">");
            }

            return $"{GetType().Name}[{sb} {_config.Target}]";
        }

        #endregion

        #region IDisposable

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(false);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
