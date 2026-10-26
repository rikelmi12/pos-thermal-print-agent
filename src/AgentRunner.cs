using System.Text;

namespace CardapioPrintAgent;

internal static class AgentRunner
{
    public static async Task RunHeadlessAsync(string[] args)
    {
        AppPaths.EnsureDirectories();

        using var instanceLock = new Mutex(false, "Global\\CardapioPrintAgentRunnerMutex", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        var logger = new AgentLogger();
        var config = AgentConfig.Load();
        config.Normalize();

        if (string.IsNullOrWhiteSpace(config.Token))
        {
            logger.Error("PRINT_AGENT_TOKEN nao configurado. Abra o app e salve um token valido.");
            return;
        }
        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            logger.Error("PRINT_AGENT_SERVER_URL nao configurado. Abra o app e salve uma URL valida.");
            return;
        }

        var api = new PrintAgentApi(config, logger);
        var state = new AgentStateStore(logger);

        using var cts = new CancellationTokenSource();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        AgentProcessManager.WritePidFile(Environment.ProcessId);
        logger.Info($"Agente nativo {AppVersion.Label} iniciado em segundo plano.");
        logger.Info($"Servidor: {config.ServerUrl}");
        logger.Info($"Cliente: {config.ClientId}");
        logger.Info($"Impressora: {(string.IsNullOrWhiteSpace(config.PrinterName) ? "padrao do Windows" : config.PrinterName)}");
        logger.Info($"Formato: {config.PaperProfileLabel} ({config.PrintMode}, {config.PaperWidthMm}mm, {config.ReceiptColumns} colunas)");
        logger.Info($"Avanco final: {config.FeedAfterPrintMm}mm");

        try
        {
            var lastLoopError = string.Empty;
            var repeatedLoopErrors = 0;

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var hadJob = await ProcessNextJobAsync(config, api, state, logger, cts.Token);
                    lastLoopError = string.Empty;
                    repeatedLoopErrors = 0;
                    var waitMs = hadJob ? 250 : config.PollMs;
                    await Task.Delay(waitMs, cts.Token);
                }
                catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    var message = error.Message;
                    if (string.Equals(message, lastLoopError, StringComparison.Ordinal))
                    {
                        repeatedLoopErrors += 1;
                    }
                    else
                    {
                        lastLoopError = message;
                        repeatedLoopErrors = 1;
                    }

                    if (repeatedLoopErrors == 1 || repeatedLoopErrors % 10 == 0)
                    {
                        var suffix = repeatedLoopErrors > 1 ? $" (repetiu {repeatedLoopErrors}x)" : string.Empty;
                        logger.Error($"Erro no loop do agente: {message}{suffix}");
                    }

                    try
                    {
                        await Task.Delay(config.ErrorPollMs, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            AgentProcessManager.ClearPidFile();
            logger.Info("Agente encerrado.");
        }
    }

    private static async Task<bool> ProcessNextJobAsync(
        AgentConfig config,
        PrintAgentApi api,
        AgentStateStore state,
        AgentLogger logger,
        CancellationToken cancellationToken)
    {
        var job = await api.ClaimNextJobAsync(cancellationToken);
        if (job is null || job.Id <= 0)
        {
            return false;
        }

        var label = string.IsNullOrWhiteSpace(job.OrderCode) ? $"job-{job.Id}" : job.OrderCode.Trim();

        if (state.WasPrinted(job))
        {
            logger.Info($"Confirmando {label} ja impresso anteriormente...");
            try
            {
                await api.CompleteJobAsync(job.Id, cancellationToken);
                await state.ClearPrintedAsync(job, cancellationToken);
                logger.Info($"OK {label}");
            }
            catch (Exception error)
            {
                logger.Error($"Falha ao confirmar {label}: {error.Message}");
            }
            return true;
        }

        try
        {
            logger.Info($"Imprimindo {label}...");
            await NativePrinter.PrintTextAsync(
                job.Text,
                config.PrinterName,
                config.PrintTimeoutMs,
                config.PrintMode,
                config.PaperWidthMm,
                config.FeedAfterPrintMm,
                cancellationToken);

            await state.MarkPrintedAsync(job, cancellationToken);
            await api.CompleteJobAsync(job.Id, cancellationToken);
            await state.ClearPrintedAsync(job, cancellationToken);

            logger.Info($"OK {label}");
            return true;
        }
        catch (TimeoutException)
        {
            logger.Error($"Tempo excedido ao imprimir {label}. Confirmando para evitar duplicidade.");
            try
            {
                await state.MarkPrintedAsync(job, cancellationToken);
                await api.CompleteJobAsync(job.Id, cancellationToken);
                await state.ClearPrintedAsync(job, cancellationToken);
            }
            catch (Exception confirmError)
            {
                logger.Error($"Comanda {label} possivelmente impressa, mas falhou ao confirmar: {confirmError.Message}");
            }
            return true;
        }
        catch (Exception error)
        {
            logger.Error($"Falha ao imprimir {label}: {error.Message}");
            try
            {
                await api.FailJobAsync(job.Id, error.Message, cancellationToken);
            }
            catch (Exception failError)
            {
                logger.Error($"Tambem falhou ao devolver {label} para fila: {failError.Message}");
            }
            return true;
        }
    }
}
