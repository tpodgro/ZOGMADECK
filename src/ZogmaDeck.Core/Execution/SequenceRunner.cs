using System.Diagnostics;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Diagnostics;

namespace ZogmaDeck.Core.Execution;

public enum StepStatus { Pending, Running, Succeeded, Failed, Skipped, Cancelled }

public enum SequenceStatus
{
    /// <summary>Toutes les étapes actives ont réussi.</summary>
    Succeeded,
    /// <summary>Terminé, mais au moins une étape en « continuer en cas d'erreur » a échoué.</summary>
    CompletedWithErrors,
    /// <summary>Arrêté par une étape en erreur.</summary>
    Failed,
    Cancelled,
}

public sealed record StepResult(int Index, string Description, StepStatus Status, string? Error, TimeSpan Duration);

public sealed record SequenceResult(SequenceStatus Status, IReadOnlyList<StepResult> Steps)
{
    public string? FirstError => Steps.FirstOrDefault(s => s.Status == StepStatus.Failed)?.Error;

    public string Summary => Status switch
    {
        SequenceStatus.Succeeded => "Terminé",
        SequenceStatus.Cancelled => "Annulé",
        SequenceStatus.CompletedWithErrors => "Terminé avec des erreurs : " + FirstError,
        _ => FirstError ?? "Échec",
    };
}

public sealed record StepProgress(int Index, int Count, string Description, StepStatus Status, string? Error = null);

/// <summary>Exécute une séquence d'actions. Ne lève jamais d'exception : tout est rapporté dans <see cref="SequenceResult"/>.</summary>
public static class SequenceRunner
{
    public static async Task<SequenceResult> RunAsync(IReadOnlyList<DeckAction> actions, ActionContext ctx, CancellationToken ct,
        IProgress<StepProgress>? progress = null)
    {
        var results = new List<StepResult>(actions.Count);
        var hadError = false;

        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            string description;
            try { description = action.Describe(); }
            catch { description = action.TypeId; }

            if (ct.IsCancellationRequested)
            {
                results.Add(new(i, description, StepStatus.Cancelled, null, TimeSpan.Zero));
                MarkRemaining(actions, i + 1, results);
                return new(SequenceStatus.Cancelled, results);
            }

            if (!action.Enabled)
            {
                results.Add(new(i, description, StepStatus.Skipped, null, TimeSpan.Zero));
                progress?.Report(new(i, actions.Count, description, StepStatus.Skipped));
                continue;
            }

            progress?.Report(new(i, actions.Count, description, StepStatus.Running));
            var sw = Stopwatch.StartNew();
            string? error = null;
            try
            {
                var problems = action.Validate().ToList();
                if (problems.Count > 0) throw new ActionException(problems[0]);
                await action.ExecuteAsync(ctx, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                results.Add(new(i, description, StepStatus.Cancelled, null, sw.Elapsed));
                progress?.Report(new(i, actions.Count, description, StepStatus.Cancelled));
                MarkRemaining(actions, i + 1, results);
                Log.Info($"Séquence annulée à l'étape {i + 1} ({description}).");
                return new(SequenceStatus.Cancelled, results);
            }
            catch (ActionException ex)
            {
                error = ex.Message;
            }
            catch (Exception ex)
            {
                error = $"Erreur inattendue : {ex.Message}";
                Log.Error($"Exception dans l'étape {i + 1} ({action.TypeId})", ex);
            }

            if (error is null)
            {
                results.Add(new(i, description, StepStatus.Succeeded, null, sw.Elapsed));
                progress?.Report(new(i, actions.Count, description, StepStatus.Succeeded));
                continue;
            }

            hadError = true;
            Log.Warn($"Étape {i + 1} « {description} » en échec : {error}");
            results.Add(new(i, description, StepStatus.Failed, error, sw.Elapsed));
            progress?.Report(new(i, actions.Count, description, StepStatus.Failed, error));
            if (action.OnError == ErrorPolicy.Stop)
            {
                MarkRemaining(actions, i + 1, results);
                return new(SequenceStatus.Failed, results);
            }
        }

        return new(hadError ? SequenceStatus.CompletedWithErrors : SequenceStatus.Succeeded, results);
    }

    private static void MarkRemaining(IReadOnlyList<DeckAction> actions, int from, List<StepResult> results)
    {
        for (var j = from; j < actions.Count; j++)
        {
            string d;
            try { d = actions[j].Describe(); } catch { d = actions[j].TypeId; }
            results.Add(new(j, d, StepStatus.Skipped, null, TimeSpan.Zero));
        }
    }
}
