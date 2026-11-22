using Newtonsoft.Json;
using QuantConnect.Optimizer;
using QuantConnect.Optimizer.Objectives;
using QuantConnect.Optimizer.Parameters;
using QuantConnect.Optimizer.Strategies;
using QuantConnect.Services.Contracts;
using QuantConnect.Util;
using ContractParameter = QuantConnect.Services.Contracts.OptimizationParameter;
using LeanParameter = QuantConnect.Optimizer.Parameters.OptimizationParameter;

namespace QuantConnect.Services.Optimizer;

/// <summary>Thrown for an optimization spec LEAN cannot run (unknown strategy, bad parameters): a final, non-retryable failure.</summary>
public sealed class InvalidOptimizationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Maps the contract's <see cref="OptimizationSpec"/> onto LEAN's <see cref="OptimizationNodePacket"/> and strategies.</summary>
public static class OptimizationPacketFactory
{
    public const string DefaultStrategy = "QuantConnect.Optimizer.Strategies.GridSearchOptimizationStrategy";

    public static OptimizationNodePacket Create(Guid optimizationId, OptimizationSpec spec, OptimizerSettings settings, DateTimeOffset? created = null)
    {
        var strategyType = StrategyType(spec.Strategy);
        try
        {
            return new OptimizationNodePacket
            {
                OptimizationId = optimizationId.ToString("D"),
                Name = spec.Name ?? string.Empty,
                Created = (created ?? DateTimeOffset.UtcNow).UtcDateTime,
                OptimizationStrategy = strategyType.FullName!,
                OptimizationStrategySettings = StrategySettings(spec, strategyType),
                Criterion = Criterion(spec.Criterion),
                Constraints = (spec.Constraints ?? new List<OptimizationConstraint>()).Select(Constraint).ToList().AsReadOnly(),
                OptimizationParameters = new HashSet<LeanParameter>(spec.Parameters.Select(Parameter)),
                MaximumConcurrentBacktests = Math.Clamp(spec.MaximumConcurrentBacktests ?? settings.DefaultConcurrentBacktests, 1, settings.MaxConcurrentBacktests),
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException or FormatException)
        {
            throw new InvalidOptimizationException($"Invalid optimization: {e.Message}", e);
        }
    }

    /// <summary>Number of backtests the strategy will generate (LEAN <c>OptimizationEstimate</c>), without submitting anything.</summary>
    public static int Estimate(OptimizationSpec spec, OptimizerSettings settings)
    {
        var packet = Create(Guid.Empty, spec, settings);
        var strategy = (IOptimizationStrategy)Activator.CreateInstance(StrategyType(spec.Strategy))!;
        try
        {
            strategy.Initialize(packet.Criterion, packet.Constraints, packet.OptimizationParameters, packet.OptimizationStrategySettings);
            return strategy.GetTotalBacktestEstimate();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOptimizationException($"Invalid optimization: {e.Message}", e);
        }
    }

    public static Type StrategyType(string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? DefaultStrategy : name.Trim();
        var type = typeof(IOptimizationStrategy).Assembly.GetType(name);
        if (type == null || type.IsAbstract || !typeof(IOptimizationStrategy).IsAssignableFrom(type))
        {
            throw new InvalidOptimizationException($"Unknown optimization strategy '{name}'");
        }
        return type;
    }

    public static Target Criterion(OptimizationCriterion criterion) =>
        new(criterion.Target, criterion.Extremum == Contracts.Extremum.Max ? new Maximization() : new Minimization(), criterion.TargetValue);

    public static Constraint Constraint(OptimizationConstraint constraint) => new(constraint.Target, constraint.Operator switch
    {
        ConstraintOperator.Equals => ComparisonOperatorTypes.Equals,
        ConstraintOperator.NotEqual => ComparisonOperatorTypes.NotEqual,
        ConstraintOperator.Less => ComparisonOperatorTypes.Less,
        ConstraintOperator.LessOrEqual => ComparisonOperatorTypes.LessOrEqual,
        ConstraintOperator.Greater => ComparisonOperatorTypes.Greater,
        ConstraintOperator.GreaterOrEqual => ComparisonOperatorTypes.GreaterOrEqual,
        _ => throw new ArgumentOutOfRangeException(nameof(constraint), constraint.Operator, "unknown constraint operator"),
    }, constraint.TargetValue);

    public static LeanParameter Parameter(ContractParameter parameter)
    {
        if (parameter.Value != null)
        {
            return new StaticOptimizationParameter(parameter.Name, parameter.Value);
        }
        if (parameter is not { Min: { } min, Max: { } max, Step: { } step })
        {
            throw new ArgumentException($"parameter '{parameter.Name}' needs either value or min, max and step");
        }
        return parameter.MinStep is { } minStep
            ? new OptimizationStepParameter(parameter.Name, min, max, step, minStep)
            : new OptimizationStepParameter(parameter.Name, min, max, step);
    }

    /// <summary>
    /// <c>strategySettings</c> as LEAN's settings type for the strategy (<see cref="StepBaseOptimizationStrategySettings"/> for the step
    /// based ones, which Euler search requires). Never with type-name handling: the JSON comes from users.
    /// </summary>
    private static OptimizationStrategySettings StrategySettings(OptimizationSpec spec, Type strategyType)
    {
        var settingsType = typeof(StepBaseOptimizationStrategy).IsAssignableFrom(strategyType)
            ? typeof(StepBaseOptimizationStrategySettings)
            : typeof(OptimizationStrategySettings);
        if (spec.StrategySettings is not { ValueKind: System.Text.Json.JsonValueKind.Object } json)
        {
            return (OptimizationStrategySettings)Activator.CreateInstance(settingsType)!;
        }
        return (OptimizationStrategySettings)JsonConvert.DeserializeObject(json.GetRawText(), settingsType,
            new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MetadataPropertyHandling = MetadataPropertyHandling.Ignore })!;
    }
}
