using Godswar.Server;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Rates; // RATES
using Godswar.Server.Networking;
using Godswar.Server.Networking.RelayGateway;
using Godswar.Server.Networking.SemanticGateway;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Operations;
using Godswar.Server.Operations.Observability;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;
using var controlledHostEvidence = ControlledHostPrivacyEvidence
    .TryInstallFromEnvironment();

if (await ServerStartupCommandDispatcher.TryRunAsync(args))
{
    return;
}
var optionsPath = args.Length > 0 ? args[0] : "appsettings.json";
if (!ServerRuntimeBootstrap.TryLoadOptions(
        optionsPath,
        out var options,
        out var runtimeProfile))
{
    return;
}
using var observability = ServerObservabilityRuntime.Start(
    options.Operations.Management.MaximumResponseBytes,
    installConsoleBoundary: controlledHostEvidence is null);
LegacyPersistenceMetrics.EnsureInitialized();
observability.RecordLifecycle("server", "starting");
try
{
    var legacyAuthenticationAccess = LegacyAuthenticationAccess.Create(
        runtimeProfile);
    var phase4AcceptanceFaults = SecurePhase4AcceptanceFaults.Create(
        options.Secure.Phase4AcceptanceFaults);
    var postgres = options.Storage.PostgresConnectionString;
    await PostgresSchemaStartup.InitializeAsync(postgres);
    await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(postgres);
    // RATES: server-wide multipliers/limits table; never fails start-up.
    await ServerRatesRuntime.InitializeAsync(postgres);
    // EXPIRY (b03): real-time item expiry; off unless the migration is applied.
    await Godswar.Server.Infrastructure.Inventory.ItemExpiryRuntime.InitializeAsync(postgres);
    // TRADE: player trade / stall store (player_trade_log); never fails start-up.
    await Godswar.Server.Infrastructure.Trade.PlayerTradeStore.InitializeAsync(postgres);
    await NpcContentRuntime.InitializeAsync(postgres);
    // EVENT DROPS (q07): dated holiday/event drop windows; none = stock drops.
    await Godswar.Server.Infrastructure.Rates.EventDropWindowRuntime.InitializeAsync(postgres);
    await Godswar.Server.Infrastructure.Events.EventBoardRuntime.InitializeAsync(postgres); // EVENTSBOARD
    await Godswar.Server.Infrastructure.Arena.AfkRevivalWallet.InitializeAsync(postgres); // ARENA
    await Godswar.Server.Infrastructure.Relations.RelationStore.InitializeAsync(postgres);
    Godswar.Server.Application.Operators.OperatorControls.UsesCharacterGmFlags = options.Game.DeveloperCommands.UseCharacterGmFlags;
    // ROUND2: operator (GM) command store (operator_log); never fails start-up.
    await Godswar.Server.Infrastructure.Operators.OperatorRuntime.InitializeAsync(postgres);
    var (realmCalendars, realmCalendar) =
        await ServerRealmCalendarStartup.LoadForProcessAsync(options);
    NpcContentRuntime.Calendar = realmCalendar;
    Godswar.Server.Application.Gates.RealmClock.Calendar = realmCalendar; // FINALISE2: spies + farm round on realm time (AEST)
    Godswar.Server.Infrastructure.EventsNpc.EventsNpcRuntime.Initialize(postgres); // EVENTSNPC
    var itemContent = await ServerItemContentComposition.LoadAsync(options);
    var petContent = await ServerPetContentComposition.LoadAsync(
        options,
        itemContent.Templates);
    var petOwnerMergeContent = await ServerPetOwnerMergeContentComposition
        .LoadAsync(options);
    var petLearnedSkillContent =
        await ServerRuntimeContentComposition.LoadLearnedSkillsAsync(options);
    var holyBalance =
        await ServerRuntimeContentComposition.LoadHolySpiritBalanceAsync(options);
    var dailyNpcs = await
        ServerDailyNpcStartup.LoadBalancesAsync(options, itemContent, petContent);
    var worldContent = await ServerWorldContentComposition.TryLoadAsync(options);
    if (worldContent is null)
    {
        return;
    }
    var runtimeContent = await ServerRuntimeBootstrapContext.LoadAsync(
        options, worldContent, itemContent, petContent, petOwnerMergeContent,
        petLearnedSkillContent, holyBalance, dailyNpcs);
    RuntimeContentCompatibilityValidator.Validate(itemContent.Templates,
        worldContent.Gameplay);
    var gameplayCatalogs = GameplayRuntimeCatalogs.Create(
        worldContent.Gameplay,
        runtimeContent.MonsterRewardPolicy);
    var gameplayContentRevision = worldContent.Manifest.Gameplay.Sha256;
    await using IGameStore store = new PostgresGameStore(
        postgres,
        itemContent,
        gameplayContentRevision,
        petContent,
        petLearnedSkillContent: petLearnedSkillContent,
        holySpiritBalance: holyBalance,
        realmId: options.Game.WorldInstances.ProcessRealmId);
    await using var applicationData =
        runtimeContent.CreateApplicationData(realmCalendar);
    var accountPersistence =
        ServerAccountPersistenceComposition.Create(applicationData);
    await using AccountAuthenticationService? rawAuthentication =
        legacyAuthenticationAccess is not null
            ? AccountAuthenticationService.CreateLegacyRaw(
                accountPersistence.Credentials,
                accountPersistence.Presence,
                options.Authentication)
            : null;
    await using AccountAuthenticationService? secureAuthentication =
        options.Secure.Enabled
            ? new AccountAuthenticationService(
                accountPersistence.Credentials,
                accountPersistence.Presence,
                options.Authentication)
            : null;
    var gameplayPersistence =
        ServerGameplayPersistenceComposition.Create(applicationData);
    using var shutdown = new CancellationTokenSource();
    var controlledHostShutdown =
        ControlledHostShutdownControl.TryCreateFromEnvironment(
            options,
            controlledHostEvidence is not null,
            shutdown);
    await using var coordination =
        await runtimeContent.CreateCoordinationAsync(
            realmCalendars,
            shutdown.Token);
    await using var characterCheckpoints =
        new CharacterCheckpointCoordinator(
            gameplayPersistence.CharacterCheckpoints,
            options.Storage.Checkpoints);
    var registry = new GameSessionRegistry(
        store,
        options.Game.ZodiacEnergy,
        options.Game.Monsters.Runtime,
        options.Game.Players.Runtime,
        characterCheckpoints,
        applicationData.ProgressionIntervalSettlementCommands,
        zodiacLevelStore: gameplayPersistence.ZodiacLevels,
        experienceBoosts: gameplayPersistence.ExperienceBoosts,
        characterRuntimeProjections:
            gameplayPersistence.CharacterRuntime,
        requiresDurablePlayerPersistence: true,
        worldInstanceOptions: options.Game.WorldInstances,
        gameplayCatalogs: gameplayCatalogs,
        itemContent: itemContent,
        trainingDummies: TrainingDummyPolicy.Create(
            options.Game.TrainingDummies,
            runtimeProfile),
        guildAltarSettlements: applicationData.Guilds);
    // ROUND2: loopback /admin live-edit endpoints on the management port.
    Godswar.Server.Application.Operators.OperatorAdminApi.Handler =
        (request, token) => registry.HandleOperatorAdminAsync(
            request,
            itemContent.Templates,
            token);
    var gameHandlerFactory = new GameClientHandlerFactory(
        store,
        accountPersistence.Directory,
        accountPersistence.Presence,
        registry,
        gameplayPersistence.CharacterSnapshots,
        gameplayPersistence.SealedPetSnapshots,
        worldContent,
        options.Game.DeveloperCommands,
        characterCheckpoints,
        gameplayPersistence,
        applicationData,
        realmCalendar,
        coordination.Worker,
        gameplayCatalogs,
        itemContent,
        petContent,
        processRealmId: options.Game.WorldInstances.ProcessRealmId);
    var admission = new ConnectionAdmission(new ConnectionAdmissionOptions(
        options.Network.MaxActiveConnections,
        options.Network.MaxUnauthenticatedConnections,
        options.Network.MaxUnauthenticatedConnectionsPerIp,
        options.Network.MaxUnauthenticatedConnectionsPerPrefix));
    using var workerBackhaulRuntime =
        WorkerBackhaulRuntime.TryCreate(
            options,
            admission,
            gameHandlerFactory);
    var listenerProfile = workerBackhaulRuntime is null
        ? ServerListenerProfile.Build(options)
        : null;
    var (loginServer, gameServer) =
        ServerRuntimeListenerFactory.CreateRawPair(
            listenerProfile,
            options,
            admission,
            session => new LoginClientHandler(
                session,
                options,
                rawAuthentication ??
                    throw new InvalidOperationException(
                        "Raw login authentication is unavailable."),
                legacyAuthenticationAccess:
                    legacyAuthenticationAccess,
                realmCatalog:
                    applicationData.RealmCatalog),
            session => gameHandlerFactory.Create(
                session,
                legacyAuthenticationAccess:
                    legacyAuthenticationAccess));

    using SecureServerCertificate? secureCertificate =
        options.Secure.Enabled
            ? SecureServerCertificate.Load(options.Secure)
            : null;
    var secureGameTarget = options.Secure.Enabled
        ? options.Secure.BuildGameTarget()
        : null;
    await using SecureUdpRuntime? secureUdpRuntime =
        secureGameTarget is not null
            ? SecureUdpRuntime.TryCreate(
                options.Secure,
                secureGameTarget,
                SecureUdpRuntimeCapabilities.Current,
                phase4AcceptanceFaults:
                    phase4AcceptanceFaults)
            : null;
    IGameTicketStore? secureGameTickets =
        options.Secure.Enabled
            ? coordination.CreateGameTicketStore(
                options.Secure.Tickets)
            : null;
    using var operationalStateMetrics = new OperationalStateMetrics(
        admission,
        secureGameTickets as IGameTicketStoreSnapshotSource,
        secureUdpRuntime,
        coordination.Worker);
    var serverOperationalState = new ServerOperationalState(
        ServerReadinessDependency.All);
    serverOperationalState.SetDependency(
        ServerReadinessDependency.SchemaAndContent,
        ready: true);
    var drainCoordinator = new ServerDrainCoordinator(
        serverOperationalState,
        admission,
        shutdown,
        options.Network.GracefulDrainTimeout);
    ManagementDrainResult BeginRuntimeDrain()
    {
        workerBackhaulRuntime?.BeginDrain();
        coordination.Worker?.BeginDrain();
        return drainCoordinator.BeginDrain();
    }

    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        BeginRuntimeDrain();
    };
    using var processSignals =
        ServerProcessSignalRegistration.Install(
            () => BeginRuntimeDrain());
    if (!ServerManagementTokenComposition.TryLoad(
            options,
            observability,
            out var loadedManagementToken))
    {
        return;
    }
    using var managementToken = loadedManagementToken;
    var taskSupervisor = new CriticalTaskSupervisor(
        serverOperationalState,
        shutdown.Cancel,
        observability.RecordCriticalTask);
    using var serverOperationsMetrics =
        new ServerOperationsMetrics(
            serverOperationalState,
            taskSupervisor,
            observability.RecordManagement);
    var managementServer = options.Operations.Management.Enabled
        ? new ManagementHttpServer(
            options.Operations.Management,
            serverOperationalState.GetSnapshot,
            observability.GetMetricsAsync,
            observability.GetTracesAsync,
            token => managementToken?.Authenticate(token) == true,
            _ => ValueTask.FromResult(
                BeginRuntimeDrain()),
            serverOperationsMetrics.RecordManagement)
        : null;
    var readinessMonitor = new ServerReadinessMonitor(
        serverOperationalState,
        options.Operations.Readiness,
        characterCheckpoints,
        registry,
        applicationData,
        applicationData.OutboxEnabled,
        options.Game.ZodiacEnergy.Enabled,
        secureUdpRuntime,
        coordination.Worker,
        observability.RecordOperationalState);
    using var progressionRetryMetrics =
        new DurableProgressionRetryMetrics(
            registry.GetDurableProgressionRetrySnapshot);
    using TlsHandshakeGate? secureHandshakeGate =
        options.Secure.Enabled
            ? new TlsHandshakeGate(options.Network.MaxConcurrentTlsHandshakes)
            : null;
    var secureTransportFactory =
        secureCertificate is not null && secureHandshakeGate is not null
            ? new TlsMuxLegacyTransportFactory(
                options.Secure,
                options.Network,
                secureCertificate.Context,
                secureHandshakeGate,
                ticketStore: secureGameTickets,
                gameTarget: secureGameTarget,
                udpSessionAuthority: secureUdpRuntime?.Authority)
            : null;
    var secureLoginServer = secureTransportFactory is null
        ? null
        : new TcpEndpointServer(
            NetworkEndpointRole.Login,
            listenerProfile!.Login.Host,
            listenerProfile.Login.Port,
            options.Network,
            admission,
            session => new LoginClientHandler(
                session,
                options,
                secureAuthentication ??
                    throw new InvalidOperationException(
                        "Secure login authentication is unavailable."),
                secureGameTickets,
                secureGameTarget,
                realmCatalog:
                    applicationData.RealmCatalog),
            transportFactory: secureTransportFactory);
    var secureGameServer = secureTransportFactory is null
        ? null
        : new TcpEndpointServer(
            NetworkEndpointRole.Game,
            listenerProfile!.Game.Host,
            listenerProfile.Game.Port,
            options.Network,
            admission,
            session => gameHandlerFactory.Create(
                session,
                phase4AcceptanceFaults),
            transportFactory: secureTransportFactory);

    var endpointServers = new List<TcpEndpointServer>(2);
    var criticalTasks = new CriticalTaskCollection(
        taskSupervisor,
        shutdown.Token);
    var auxiliaryTasks = new List<Task>(1);
    Task? checkpointTask = null;
    var fatalRuntimeFailure = false;

    try
    {
        checkpointTask = taskSupervisor.RunAsync(
            CriticalTaskKind.CheckpointWorker,
            _ => characterCheckpoints.RunAsync(),
            shutdown.Token);
        await characterCheckpoints.WaitUntilReadyAsync(
            shutdown.Token).WaitAsync(
                TimeSpan.FromSeconds(10),
                shutdown.Token);
        if (coordination.Worker is not null)
        {
            criticalTasks.Start(
                CriticalTaskKind.RedisCoordination,
                coordination.Worker.RunAsync);
            await coordination.Worker.WaitUntilRegisteredAsync(
                shutdown.Token).WaitAsync(
                    TimeSpan.FromSeconds(10),
                    shutdown.Token);
        }

        criticalTasks.Start(CriticalTaskKind.MonsterWorld, registry.RunMonsterRoamingAsync);
        criticalTasks.Start(CriticalTaskKind.PlayerRecovery, registry.RunPlayerRecoveryAsync);
        criticalTasks.Start(CriticalTaskKind.PopulationNotices, token => registry.RunPopulationNoticesAsync(realmCalendar, token));
        auxiliaryTasks.Add(registry.RunEventBoardAsync(realmCalendar, shutdown.Token)); // EVENTSBOARD: off unless event.board = 1
        auxiliaryTasks.Add(registry.RunHeraLabyrinthAnnouncementAsync(realmCalendar, shutdown.Token)); // HERA: off unless GODSWAR_HERA_LABYRINTH=1
        // The altar drain has to be applied whether or not the member is online, so
        // it runs on the server's own clock rather than on a read.
        criticalTasks.Start(
            CriticalTaskKind.GuildAltarSettlement,
            registry.RunGuildAltarSettlementAsync);
        if (store is Godswar.Server.State.ILelantineFarmRoundStore farmRounds)
        {
            _ = registry.RunLelantineFarmRoundsAsync(farmRounds, shutdown.Token);
        }
        if (store is Godswar.Server.State.IBattlefieldRoundStore battlefieldRounds)
        {
            _ = registry.RunBattlefieldRoundsAsync(battlefieldRounds, shutdown.Token);
        }
        criticalTasks.Start(CriticalTaskKind.ExperienceBoostReconciliation, registry.RunExperienceBoostStatusReconciliationAsync);
        if (options.Game.ZodiacEnergy.Enabled)
        {
            criticalTasks.Start(
                CriticalTaskKind.ZodiacEnergyAccrual,
                registry.RunZodiacEnergyAccrualAsync);
        }
        criticalTasks.Start(CriticalTaskKind.DurableProgressionRetry, registry.RunDurableProgressionRetryAsync);
        criticalTasks.Start(CriticalTaskKind.LegacyInstanceOpalRecovery, applicationData.RunLegacyInstanceOpalRecoveryAsync);
        if (applicationData.OutboxEnabled)
        {
            criticalTasks.Start(CriticalTaskKind.OutboxDispatcher,
                applicationData.RunOutboxAsync);
        }
        ServerReconciliationComposition.StartWorkerIfEnabled(
            criticalTasks,
            applicationData);

        if (secureUdpRuntime is not null)
        {
            criticalTasks.Start(
                CriticalTaskKind.SecureUdp,
                secureUdpRuntime.RunAsync);
        }

        if (loginServer is not null && gameServer is not null)
        {
            endpointServers.Add(loginServer);
            endpointServers.Add(gameServer);
            criticalTasks.Start(CriticalTaskKind.LoginListener, loginServer.RunAsync);
            criticalTasks.Start(CriticalTaskKind.GameListener, gameServer.RunAsync);
        }
        if (secureLoginServer is not null && secureGameServer is not null)
        {
            endpointServers.Add(secureLoginServer);
            endpointServers.Add(secureGameServer);
            criticalTasks.Start(CriticalTaskKind.LoginListener, secureLoginServer.RunAsync);
            criticalTasks.Start(CriticalTaskKind.GameListener, secureGameServer.RunAsync);
        }
        if (workerBackhaulRuntime is not null)
        {
            workerBackhaulRuntime.Start(
                endpointServers,
                criticalTasks);
        }

        WorkerBackhaulRuntime.ValidateListenerComposition(
            workerBackhaulRuntime,
            endpointServers.Count);

        if (managementServer is not null)
        {
            criticalTasks.Start(
                CriticalTaskKind.ManagementHttp,
                managementServer.RunAsync);
        }
        criticalTasks.Start(
            CriticalTaskKind.PostgresReadiness,
            readinessMonitor.RunAsync);
        // RATES: hot reload of public.server_rates (not a critical task: a
        // failing poll keeps the last good rates and never stops the server).
        auxiliaryTasks.Add(ServerRatesRuntime.RunAsync(shutdown.Token));
        auxiliaryTasks.Add(NpcContentRuntime.RunAsync(shutdown.Token));
        auxiliaryTasks.Add(Godswar.Server.Infrastructure.Rates.EventDropWindowRuntime.RunAsync(shutdown.Token));
        taskSupervisor.SealRegistrations();

        await ServerListenerReadiness.WaitAsync(endpointServers,
            managementServer, secureUdpRuntime, readinessMonitor, shutdown.Token);
        serverOperationalState.SetDependency(
            ServerReadinessDependency.ListenerProfile,
            ready: true);
        if (coordination.Worker is not null)
        {
            await coordination.Worker.PublishAvailableAsync(
                shutdown.Token);
            await coordination.Worker.WaitUntilReadyAsync(
                shutdown.Token).WaitAsync(
                    TimeSpan.FromSeconds(10),
                    shutdown.Token);
        }
        await readinessMonitor.RefreshAsync(shutdown.Token);
        serverOperationalState.TryMarkRunning();
        ControlledHostPrivacyEvidence.RecordIfActive(
            ControlledHostEvidenceEvent.SecureListenersReady);
        observability.RecordLifecycle(
            "server",
            serverOperationalState.GetSnapshot().IsReady
                ? "ready"
                : "running_not_ready");

        ServerRuntimeShutdown.StartControlledHostShutdown(
            controlledHostShutdown,
            shutdown,
            auxiliaryTasks);

        await Task.WhenAll(
            criticalTasks.Items.Concat(auxiliaryTasks));
    }
    catch
    {
        fatalRuntimeFailure = true;
        serverOperationalState.MarkCriticalTaskFaulted();
        observability.RecordLifecycle(
            "server",
            "faulted",
            OperationalLogLevel.Critical);
    }
    finally
    {
        workerBackhaulRuntime?.BeginDrain();
        coordination.Worker?.BeginDrain();
        admission.BeginDrain();
        serverOperationalState.TryMarkStopping();
        var shutdownTasks = criticalTasks.Items
            .Concat(auxiliaryTasks)
            .Concat(
                checkpointTask is null
                    ? []
                    : [checkpointTask]);
        if (!await CriticalTaskShutdown.CompleteAsync(
                characterCheckpoints,
                shutdown,
                shutdownTasks,
                options.Storage.Checkpoints.ShutdownDrainTimeout))
        {
            fatalRuntimeFailure = true;
        }
        if (!await ServerRuntimeShutdown.TryDisposeWorldInstancesAsync(
                registry, observability))
        {
            fatalRuntimeFailure = true;
        }
        serverOperationalState.MarkStopped();
    }

    ServerRuntimeShutdown.SetProcessOutcome(fatalRuntimeFailure, observability);
}
catch (Exception error)
{
    StartupFailureDiagnostics.Record(observability.Logger, error);
    observability.RecordLifecycle(
        "server",
        "startup_failed",
        OperationalLogLevel.Critical);
    Environment.ExitCode = 4;
}
