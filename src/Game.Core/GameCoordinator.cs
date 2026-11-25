namespace Game.Core;

public sealed class GameCoordinator
{
    private static readonly TimeSpan SimulatedRollDelay = TimeSpan.FromSeconds(2);
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IDiceRoller _diceRoller;
    private readonly IGameEventPublisher _eventPublisher;
    private GameState? _game;

    public GameCoordinator(IDiceRoller diceRoller, IGameEventPublisher eventPublisher)
    {
        _diceRoller = diceRoller;
        _eventPublisher = eventPublisher;
    }

    public async Task<CreateGameResult> CreateGameAsync(
        string name,
        int playerCount,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            lock (_stateLock)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new GameRuleException(GameError.InvalidName, "El nombre de la partida es obligatorio.");

                if (playerCount is < 2 or > 4)
                    throw new GameRuleException(GameError.InvalidPlayerCount, "La partida debe tener entre 2 y 4 jugadores.");

                if (_game is not null && _game.Status != GameStatus.Finished)
                    throw new GameRuleException(GameError.GameAlreadyActive, "Ya hay una partida activa.");

                var creator = new PlayerState(Guid.NewGuid(), 1);
                _game = new GameState(Guid.NewGuid(), name.Trim(), playerCount, creator);
                var snapshot = GameSnapshots.Create(_game);
                return new CreateGameResult(_game.Id, creator.Id, snapshot);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<JoinGameResult> JoinGameAsync(
        string gameName,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            JoinGameResult result;
            IReadOnlyList<GameEvent> events;

            lock (_stateLock)
            {
                var game = GetGameByName(gameName);
                if (game.Status != GameStatus.WaitingForPlayers)
                    throw new GameRuleException(GameError.GameFull, "La partida ya no admite jugadores.");

                var player = new PlayerState(Guid.NewGuid(), game.Players.Count + 1);
                game.Players.Add(player);
                game.PlayersById[player.Id] = player;
                game.TurnOrder.Enqueue(player);

                if (game.Players.Count == game.Capacity)
                {
                    game.Status = GameStatus.InProgress;
                    game.NotifyTurnChanged();
                    game.TurnOrder.TryPeek(out var firstPlayer);
                    events =
                    [
                        new TurnChangedEvent(game.Id, firstPlayer?.Number, null),
                        new ScoresUpdatedEvent(game.Id, GameSnapshots.CreateScores(game), null)
                    ];
                }
                else
                {
                    events = [];
                }

                result = new JoinGameResult(player.Id, GameSnapshots.Create(game));
            }

            var deliveryWarning = await PublishEventsAsync(events, cancellationToken);
            return result with { EventDeliveryWarning = deliveryWarning };
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public GameSnapshot GetState(Guid gameId)
    {
        lock (_stateLock)
        {
            var game = GetGameById(gameId);
            return GameSnapshots.Create(game);
        }
    }

    public async Task<RollResult> RollAsync(
        Guid gameId,
        Guid playerId,
        CancellationToken cancellationToken = default)
    {
        var waitedForTurn = false;
        while (true)
        {
            await _operationGate.WaitAsync(cancellationToken);
            Task? turnChanged = null;
            GameState? gameToRoll = null;
            PlayerState? playerToRoll = null;
            try
            {
                lock (_stateLock)
                {
                    var game = GetGameById(gameId);
                    if (game.Status == GameStatus.Finished)
                    {
                        throw new GameRuleException(GameError.GameFinished, "La partida ya ha finalizado.");
                    }

                    if (!game.PlayersById.ContainsKey(playerId))
                    {
                        throw new GameRuleException(GameError.NotPlayersTurn, "El jugador no pertenece a esta partida.");
                    }

                    if (game.Status == GameStatus.WaitingForPlayers)
                    {
                        turnChanged = game.TurnChanged;
                    }
                    else if (game.Status != GameStatus.InProgress)
                    {
                        throw new GameRuleException(GameError.GameNotStarted, "La partida todavía no ha comenzado.");
                    }
                    else if (!game.TurnOrder.TryPeek(out var currentPlayer))
                    {
                        throw new InvalidOperationException("La partida no tiene un jugador activo.");
                    }
                    else if (currentPlayer.Id != playerId)
                    {
                        turnChanged = game.TurnChanged;
                    }
                    else
                    {
                        gameToRoll = game;
                        playerToRoll = currentPlayer;
                    }
                }

                if (turnChanged is null)
                {
                    if (waitedForTurn)
                    {
                        // Simula un retraso en la tirada de dados para que el jugador vea el resultado de su tirada.
                        await Task.Delay(SimulatedRollDelay, cancellationToken);
                    }

                    var game = gameToRoll ??
                        throw new InvalidOperationException("No se encontró la partida para la tirada.");
                    var currentPlayer = playerToRoll ??
                        throw new InvalidOperationException("No se encontró el jugador activo para la tirada.");
                    RollResult result;
                    IReadOnlyList<GameEvent> events;
                    lock (_stateLock)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var (first, second) = _diceRoller.Roll();
                        if (first is < 1 or > 6 || second is < 1 or > 6)
                        {
                            throw new InvalidOperationException("El lanzador de dados debe devolver valores entre 1 y 6.");
                        }

                        currentPlayer.Score += first + second;
                        if (currentPlayer.Score >= 20)
                        {
                            game.Status = GameStatus.Finished;
                            game.WinnerId = currentPlayer.Id;
                            events =
                            [
                                new TurnChangedEvent(game.Id, null, currentPlayer.Number),
                                new ScoresUpdatedEvent(game.Id, GameSnapshots.CreateScores(game), currentPlayer.Id)
                            ];
                        }
                        else
                        {
                            if (!game.TurnOrder.TryDequeue(out var dequeued) || dequeued.Id != currentPlayer.Id)
                            {
                                throw new InvalidOperationException("El orden de turnos de la partida no es consistente.");
                            }

                            game.TurnOrder.Enqueue(currentPlayer);
                            game.TurnOrder.TryPeek(out var nextPlayer);
                            events =
                            [
                                new TurnChangedEvent(game.Id, nextPlayer?.Number, null),
                                new ScoresUpdatedEvent(game.Id, GameSnapshots.CreateScores(game), null)
                            ];
                        }

                        result = new RollResult(currentPlayer.Id, first, second, GameSnapshots.Create(game));
                        game.NotifyTurnChanged();
                    }

                    var deliveryWarning = await PublishEventsAsync(
                        events,
                        cancellationToken);
                    return result with
                    {
                        EventDeliveryWarning = deliveryWarning
                    };
                }
            }
            finally
            {
                _operationGate.Release();
            }

            await turnChanged.WaitAsync(cancellationToken);
            waitedForTurn = true;
        }
    }

    private async Task<string?> PublishEventsAsync(
        IReadOnlyList<GameEvent> events,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        foreach (var gameEvent in events)
        {
            try
            {
                await _eventPublisher.PublishAsync(gameEvent, cancellationToken);
            }
            catch (GameEventDeliveryException exception)
            {
                warnings.Add(exception.Message);
            }
        }

        return warnings.Count == 0
            ? null
            : string.Join(" ", warnings.Distinct(StringComparer.Ordinal));
    }

    private GameState GetGameByName(string name)
    {
        if (_game is null || !string.Equals(_game.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new GameRuleException(GameError.GameNotFound, "No se encontró una partida activa con ese nombre.");
        }

        return _game;
    }

    private GameState GetGameById(Guid gameId)
    {
        if (_game is null || _game.Id != gameId)
        {
            throw new GameRuleException(GameError.GameNotFound, "No se encontró una partida con ese identificador.");
        }

        return _game;
    }
}
