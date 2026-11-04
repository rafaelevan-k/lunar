using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Conversion.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Conversion.UseCases;

/// <summary>What starting a conversion did: the plan (so the screen can show what was refused or skipped and why) and the queued jobs.</summary>
/// <param name="Plan">What planning decided for every file of the request.</param>
/// <param name="JobIds">The ids of the queued jobs, in the order of <see cref="ConversionPlan.Jobs"/>; empty when nothing was left to convert.</param>
public sealed record ConversionStart(ConversionPlan Plan, IReadOnlyList<int> JobIds);

/// <summary>
/// The one entry point the screens use to convert (BE-27, PRD FR-CON-01 to FR-CON-05): plans the request, and queues what can be
/// converted. Progress, cancelling and trying again go through <see cref="IConversionQueue"/>, which the screens hold too. It knows
/// nothing of windows, files or converters, only the two ports.
///
/// A file that cannot be converted does not fail the call: it is listed in the plan with its reason, and the others are queued. The
/// call fails (with a message that says what to do) only when the request as a whole is unusable, when a choice is invalid, or when
/// the queue has no room for all the jobs (all or none are queued, never part of a batch). Cancelling the token while planning
/// queues nothing.
/// </summary>
public sealed class StartConversion
{
    private readonly IConversionPlanner _planner;
    private readonly IConversionQueue _queue;

    public StartConversion(IConversionPlanner planner, IConversionQueue queue)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(queue);
        _planner = planner;
        _queue = queue;
    }

    public async Task<Result<ConversionStart>> ExecuteAsync(ConversionRequest request, ConversionChoices? choices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chosen = choices ?? ConversionChoices.Default;
        try
        {
            chosen.Validated();
        }
        catch (ArgumentException ex) when (ex is ArgumentOutOfRangeException or ArgumentNullException)
        {
            return Result.Failure<ConversionStart>(new OperationError(ErrorCode.InvalidInput, "One of the conversion choices is not valid."));
        }

        var planned = await _planner.PlanAsync(request, ct).ConfigureAwait(false);
        if (planned.IsCancelled)
            return Result.Cancelled<ConversionStart>();
        if (planned.IsFailure)
            return Result.Failure<ConversionStart>(planned.Error!);

        var plan = planned.Value;
        if (plan.IsEmpty)
            return Result.Success(new ConversionStart(plan, []));

        // The choices are attached here, after planning, so the planner never sees a password.
        var jobs = new List<PlannedConversion>(plan.Jobs.Count);
        foreach (var job in plan.Jobs)
            jobs.Add(job with { Choices = chosen, Limits = plan.Limits });
        var withChoices = plan with { Jobs = jobs };

        if (ct.IsCancellationRequested)
            return Result.Cancelled<ConversionStart>();

        var queued = _queue.Enqueue(jobs);
        return queued.IsSuccess
            ? Result.Success(new ConversionStart(withChoices, queued.Value))
            : Result.Failure<ConversionStart>(queued.Error!);
    }
}
