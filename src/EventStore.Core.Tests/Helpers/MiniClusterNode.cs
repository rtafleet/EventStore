﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using EventStore.Common.Options;
using EventStore.Common.Utils;
using EventStore.Core.Authentication;
using EventStore.Core.Authentication.InternalAuthentication;
using EventStore.Core.Authorization;
using EventStore.Core.Bus;
using EventStore.Core.Cluster.Settings;
using EventStore.Core.Messages;
using EventStore.Core.Services.Gossip;
using EventStore.Core.Services.Monitoring;
using EventStore.Core.Tests.Http;
using EventStore.Core.Tests.Services.Transport.Tcp;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.Services.Transport.Http.Controllers;
using EventStore.Core.Util;
using EventStore.Core.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.TestHost;
using ILogger = Serilog.ILogger;

namespace EventStore.Core.Tests.Helpers {
	public class MiniClusterNode {
		public static int RunCount = 0;
		public static readonly Stopwatch RunningTime = new Stopwatch();
		public static readonly Stopwatch StartingTime = new Stopwatch();
		public static readonly Stopwatch StoppingTime = new Stopwatch();

		public const int ChunkSize = 1024 * 1024;
		public const int CachedChunkSize = ChunkSize + ChunkHeader.Size + ChunkFooter.Size;

		private static readonly ILogger Log = Serilog.Log.ForContext<MiniClusterNode>();

		public IPEndPoint InternalTcpEndPoint { get; private set; }
		public IPEndPoint InternalTcpSecEndPoint { get; private set; }
		public IPEndPoint ExternalTcpEndPoint { get; private set; }
		public IPEndPoint ExternalTcpSecEndPoint { get; private set; }
		public IPEndPoint HttpEndPoint { get; private set; }

		public readonly int DebugIndex;

		public readonly ClusterVNode Node;
		public readonly TFChunkDb Db;
		private readonly string _dbPath;
		private readonly bool _isReadOnlyReplica;
		private readonly TaskCompletionSource<bool> _started = new TaskCompletionSource<bool>();
		private readonly TaskCompletionSource<bool> _adminUserCreated = new TaskCompletionSource<bool>();

		public Task Started => _started.Task;
		public Task AdminUserCreated => _adminUserCreated.Task;

		public VNodeState NodeState = VNodeState.Unknown;
		private readonly IWebHost _host;

		private TestServer _kestrelTestServer;

		private static bool EnableHttps() {
			return !RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
		}

		public MiniClusterNode(
			string pathname, int debugIndex, IPEndPoint internalTcp, IPEndPoint internalTcpSec,
			IPEndPoint externalTcp, IPEndPoint externalTcpSec, IPEndPoint httpEndPoint, EndPoint[] gossipSeeds,
			ISubsystem[] subsystems = null, int? chunkSize = null, int? cachedChunkSize = null,
			bool enableTrustedAuth = false, bool skipInitializeStandardUsersCheck = true, int memTableSize = 1000,
			bool inMemDb = true, bool disableFlushToDisk = false, bool readOnlyReplica = false) {
			
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
				AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport",
					true); //TODO JPB Remove this sadness when dotnet core supports kestrel + http2 on macOS
			}
			
			RunningTime.Start();
			RunCount += 1;

			DebugIndex = debugIndex;

			_dbPath = Path.Combine(
				pathname,
				string.Format(
					"mini-cluster-node-db-{0}-{1}-{2}", externalTcp.Port, externalTcpSec.Port, httpEndPoint.Port));

			Directory.CreateDirectory(_dbPath);
			FileStreamExtensions.ConfigureFlush(disableFlushToDisk);
			Db =
				new TFChunkDb(
					CreateDbConfig(chunkSize ?? ChunkSize, _dbPath, cachedChunkSize ?? CachedChunkSize, inMemDb));

			InternalTcpEndPoint = internalTcp;
			InternalTcpSecEndPoint = internalTcpSec;

			ExternalTcpEndPoint = externalTcp;
			ExternalTcpSecEndPoint = externalTcpSec;
			HttpEndPoint = httpEndPoint;

			var useHttps = EnableHttps();
			var certificate = useHttps ? ssl_connections.GetServerCertificate() : null;
			var trustedRootCertificates =
				useHttps ? new X509Certificate2Collection(ssl_connections.GetRootCertificate()) : null;

			var singleVNodeSettings = new ClusterVNodeSettings(
				Guid.NewGuid(), debugIndex, () => new ClusterNodeOptions(),
				InternalTcpEndPoint, InternalTcpSecEndPoint, ExternalTcpEndPoint,
				ExternalTcpSecEndPoint, HttpEndPoint,
				new Data.GossipAdvertiseInfo(
					InternalTcpEndPoint.ToDnsEndPoint(),
					InternalTcpSecEndPoint.ToDnsEndPoint(),
					ExternalTcpEndPoint.ToDnsEndPoint(), ExternalTcpSecEndPoint.ToDnsEndPoint(), HttpEndPoint.ToDnsEndPoint(),
					null, null, 0, null, 0, 0), enableTrustedAuth,
				certificate, trustedRootCertificates, Opts.CertificateReservedNodeCommonNameDefault, 1, false,
				"", gossipSeeds, TFConsts.MinFlushDelayMs, 3, 2, 2, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10),
				TimeSpan.FromSeconds(10), false, false,TimeSpan.FromHours(1), StatsStorage.None, 0,
				new AuthenticationProviderFactory(components => 
					new InternalAuthenticationProviderFactory(components)),
				new AuthorizationProviderFactory(components =>
					new LegacyAuthorizationProviderFactory(components.MainQueue)),
				disableScavengeMerging: true, scavengeHistoryMaxAge: 30,
				adminOnPublic: true,
				statsOnPublic: true, gossipOnPublic: true, gossipInterval: TimeSpan.FromSeconds(2),
				gossipAllowedTimeDifference: TimeSpan.FromSeconds(1), gossipTimeout: TimeSpan.FromSeconds(3),
				extTcpHeartbeatTimeout: TimeSpan.FromSeconds(2), extTcpHeartbeatInterval: TimeSpan.FromSeconds(2),
				intTcpHeartbeatTimeout: TimeSpan.FromSeconds(2), intTcpHeartbeatInterval: TimeSpan.FromSeconds(2), 
				 deadMemberRemovalPeriod: TimeSpan.FromSeconds(1800),
				verifyDbHash: false, maxMemtableEntryCount: memTableSize,
				hashCollisionReadLimit: Opts.HashCollisionReadLimitDefault,
				startStandardProjections: false, disableHTTPCaching: false, logHttpRequests: false,
				connectionPendingSendBytesThreshold: Opts.ConnectionPendingSendBytesThresholdDefault,
				connectionQueueSizeThreshold: Opts.ConnectionQueueSizeThresholdDefault,
				readOnlyReplica: readOnlyReplica,
				ptableMaxReaderCount: Constants.PTableMaxReaderCountDefault,
				streamInfoCacheCapacity: Opts.StreamInfoCacheCapacityDefault,
				enableExternalTCP: true,
				disableHttps: !useHttps,
				enableAtomPubOverHTTP: true);
			_isReadOnlyReplica = readOnlyReplica;

			Log.Information(
				"\n{0,-25} {1} ({2}/{3}, {4})\n" + "{5,-25} {6} ({7})\n" + "{8,-25} {9} ({10}-bit)\n"
				+ "{11,-25} {12}\n" + "{13,-25} {14}\n" + "{15,-25} {16}\n" + "{17,-25} {18}\n" + "{19,-25} {20}\n\n",
				"ES VERSION:", VersionInfo.Version, VersionInfo.Branch, VersionInfo.Hashtag, VersionInfo.Timestamp,
				"OS:", OS.OsFlavor, Environment.OSVersion, "RUNTIME:", OS.GetRuntimeVersion(),
				Marshal.SizeOf(typeof(IntPtr)) * 8, "GC:",
				GC.MaxGeneration == 0
					? "NON-GENERATION (PROBABLY BOEHM)"
					: string.Format("{0} GENERATIONS", GC.MaxGeneration + 1), "DBPATH:", _dbPath, "ExTCP ENDPOINT:",
				ExternalTcpEndPoint, "ExTCP SECURE ENDPOINT:", ExternalTcpSecEndPoint, "ExHTTP ENDPOINT:",
				HttpEndPoint);

			Node = new ClusterVNode(Db, singleVNodeSettings,
				infoControllerBuilder: new InfoControllerBuilder()
				, subsystems: subsystems,
				gossipSeedSource: new KnownEndpointGossipSeedSource(gossipSeeds));
			Node.HttpService.SetupController(new TestController(Node.MainQueue));

			_host = new WebHostBuilder()
				.UseKestrel(o => {
					o.Listen(HttpEndPoint, options => {
						if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
							options.Protocols = HttpProtocols.Http2;
						} else { 
							options.UseHttps(new HttpsConnectionAdapterOptions {
								ServerCertificate = certificate,
								ClientCertificateMode = ClientCertificateMode.AllowCertificate,
								ClientCertificateValidation = (certificate, chain, sslPolicyErrors) => {
									var (isValid, error) =
										ClusterVNode.ValidateClientCertificateWithTrustedRootCerts(certificate, chain, sslPolicyErrors, () => trustedRootCertificates);
									if (!isValid && error != null) {
										Log.Error("Client certificate validation error: {e}", error);
									}
									return isValid;
								}
							});
						}
					});
				})
				.UseStartup(Node.Startup)
				.Build();

			_kestrelTestServer = new TestServer(new WebHostBuilder()
				.UseKestrel()
				.UseStartup(Node.Startup));
		}

		public void Start() {
			StartingTime.Start();

			Node.MainBus.Subscribe(
				new AdHocHandler<SystemMessage.StateChangeMessage>(m => {
					NodeState = _isReadOnlyReplica ? VNodeState.ReadOnlyLeaderless : VNodeState.Unknown;
				}));
			if (!_isReadOnlyReplica) {
				Node.MainBus.Subscribe(
					new AdHocHandler<SystemMessage.BecomeLeader>(m => {
						NodeState = VNodeState.Leader;
						_started.TrySetResult(true);
					}));
				Node.MainBus.Subscribe(
					new AdHocHandler<SystemMessage.BecomeFollower>(m => {
						NodeState = VNodeState.Follower;
						_started.TrySetResult(true);
					}));
			} else {
				Node.MainBus.Subscribe(
					new AdHocHandler<SystemMessage.BecomeReadOnlyReplica>(m => {
						NodeState = VNodeState.ReadOnlyReplica;
						_started.TrySetResult(true);
					}));
			}

			AdHocHandler<StorageMessage.EventCommitted> waitForAdminUser = null;
			waitForAdminUser = new AdHocHandler<StorageMessage.EventCommitted>(WaitForAdminUser);
			Node.MainBus.Subscribe(waitForAdminUser);

			void WaitForAdminUser(StorageMessage.EventCommitted m) {
				if (m.Event.EventStreamId != "$user-admin") {
					return;
				}

				_adminUserCreated.TrySetResult(true);
				Node.MainBus.Unsubscribe(waitForAdminUser);
			}

			_host.Start();
			Node.Start();

		}

		public HttpClient CreateHttpClient() {
			return new HttpClient(_kestrelTestServer.CreateHandler());
		}

		public async Task Shutdown(bool keepDb = false) {
			StoppingTime.Start();
			_kestrelTestServer?.Dispose();
			await Node.StopAsync().WithTimeout(TimeSpan.FromSeconds(20));
			_host?.Dispose();
			if (!keepDb)
				TryDeleteDirectory(_dbPath);

			StoppingTime.Stop();
			RunningTime.Stop();
		}

		public void WaitIdle() {
#if DEBUG
			Node.QueueStatsManager.WaitIdle();
#endif
		}

		private void TryDeleteDirectory(string directory) {
			try {
				Directory.Delete(directory, true);
			} catch (Exception e) {
				Debug.WriteLine("Failed to remove directory {0}", directory);
				Debug.WriteLine(e);
			}
		}

		private TFChunkDbConfig CreateDbConfig(int chunkSize, string dbPath, long chunksCacheSize, bool inMemDb) {
			ICheckpoint writerChk;
			ICheckpoint chaserChk;
			ICheckpoint epochChk;
			ICheckpoint proposalChk;
			ICheckpoint truncateChk;
			ICheckpoint replicationCheckpoint = new InMemoryCheckpoint(-1);
			ICheckpoint indexCheckpoint = new InMemoryCheckpoint(-1);
			if (inMemDb) {
				writerChk = new InMemoryCheckpoint(Checkpoint.Writer);
				chaserChk = new InMemoryCheckpoint(Checkpoint.Chaser);
				epochChk = new InMemoryCheckpoint(Checkpoint.Epoch, initValue: -1);
				proposalChk = new InMemoryCheckpoint(Checkpoint.Proposal, initValue: -1);
				truncateChk = new InMemoryCheckpoint(Checkpoint.Truncate, initValue: -1);
			} else {
				var writerCheckFilename = Path.Combine(dbPath, Checkpoint.Writer + ".chk");
				var chaserCheckFilename = Path.Combine(dbPath, Checkpoint.Chaser + ".chk");
				var epochCheckFilename = Path.Combine(dbPath, Checkpoint.Epoch + ".chk");
				var proposalFilename = Path.Combine(dbPath, Checkpoint.Proposal + ".chk");
				var truncateCheckFilename = Path.Combine(dbPath, Checkpoint.Truncate + ".chk");
				writerChk = new MemoryMappedFileCheckpoint(writerCheckFilename, Checkpoint.Writer, cached: true);
				chaserChk = new MemoryMappedFileCheckpoint(chaserCheckFilename, Checkpoint.Chaser, cached: true);
				epochChk = new MemoryMappedFileCheckpoint(
					epochCheckFilename, Checkpoint.Epoch, cached: true, initValue: -1);
				proposalChk = new MemoryMappedFileCheckpoint(
					proposalFilename, Checkpoint.Proposal, cached: true, initValue: -1);
				truncateChk = new MemoryMappedFileCheckpoint(
					truncateCheckFilename, Checkpoint.Truncate, cached: true, initValue: -1);
			}

			var nodeConfig = new TFChunkDbConfig(
				dbPath, 
				new VersionedPatternFileNamingStrategy(dbPath, "chunk-"), 
				chunkSize, 
				chunksCacheSize, 
				writerChk,
				chaserChk, 
				epochChk, 
				proposalChk, 
				truncateChk, 
				replicationCheckpoint, 
				indexCheckpoint, 
				Constants.TFChunkInitialReaderCountDefault, 
				Constants.TFChunkMaxReaderCountDefault, 
				inMemDb);
			return nodeConfig;
		}
	}
}
