using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Cursorial.UI.DataViews.Shaping.Expressions;

/// <summary>
/// The public registry of custom functions for the criteria expression language (the Filter Builder /
/// text editor / conditional-format expressions). A consumer registers a function by NAME (case-
/// insensitive) that either binds a static method (<see cref="RegisterMethod"/>) or emits its own
/// expression tree (<see cref="Register"/>) from the compiled argument expressions. The compiler
/// consults this registry for any name it doesn't handle built-in (Contains/Upper/Abs/Round/Iif/
/// Min/Max/…). Process-global; register at startup. The numeric extras (Floor/Ceiling/Sqrt/Pow/Mod)
/// ship pre-registered here.
/// </summary>
public static class CriteriaFunctions
{
    /// <summary>One registered function: its arity bounds and the expression builder.</summary>
    public sealed class Registration
    {
        internal Registration(int minArgs, int maxArgs, Func<IReadOnlyList<Expression>, Expression> build)
        {
            MinArgs = minArgs;
            MaxArgs = maxArgs;
            Build = build;
        }

        /// <summary>The minimum argument count.</summary>
        public int MinArgs { get; }

        /// <summary>The maximum argument count (<see cref="int.MaxValue"/> = variadic).</summary>
        public int MaxArgs { get; }

        /// <summary>Emits the call expression from the compiled argument expressions (may throw on a
        /// type mismatch — the compiler surfaces the message as a diagnostic).</summary>
        public Func<IReadOnlyList<Expression>, Expression> Build { get; }

        internal string ArityText => MinArgs == MaxArgs ? MinArgs.ToString()
            : MaxArgs == int.MaxValue ? $"{MinArgs}+" : $"{MinArgs}–{MaxArgs}";
    }

    private static readonly ConcurrentDictionary<string, Registration> Registered = new(StringComparer.OrdinalIgnoreCase);

    static CriteriaFunctions()
    {
        RegisterUnaryDouble("Floor", nameof(Math.Floor));
        RegisterUnaryDouble("Ceiling", nameof(Math.Ceiling));
        RegisterUnaryDouble("Sqrt", nameof(Math.Sqrt));
        Register("Pow", args => Expression.Call(typeof(Math).GetMethod(nameof(Math.Pow))!, ToDouble(args[0]), ToDouble(args[1])), 2, 2);
        Register("Mod", args => Expression.Modulo(ToDouble(args[0]), ToDouble(args[1])), 2, 2);
    }

    /// <summary>Registers a function that emits its own expression tree from the argument expressions.</summary>
    public static void Register(string name, Func<IReadOnlyList<Expression>, Expression> build, int minArgs = 0, int maxArgs = int.MaxValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(build);
        Registered[name] = new Registration(minArgs, maxArgs, build);
    }

    /// <summary>Registers a function that binds a static <paramref name="method"/> — each argument is
    /// converted to the corresponding parameter type, then <c>Expression.Call</c>ed.</summary>
    public static void RegisterMethod(string name, MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!method.IsStatic)
            throw new ArgumentException("A criteria function must bind a STATIC method.", nameof(method));
        var parameters = method.GetParameters();
        Register(name, args =>
        {
            var converted = new Expression[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var target = parameters[i].ParameterType;
                converted[i] = args[i].Type == target ? args[i] : Expression.Convert(args[i], target);
            }
            return Expression.Call(method, converted);
        }, parameters.Length, parameters.Length);
    }

    /// <summary>Removes a registered function (built-ins registered elsewhere are unaffected).</summary>
    public static bool Unregister(string name) => Registered.TryRemove(name, out _);

    /// <summary>The registered function names (for editor completion / diagnostics).</summary>
    public static IReadOnlyCollection<string> Names => Registered.Keys.ToArray();

    internal static bool TryGet(string name, out Registration registration)
        => Registered.TryGetValue(name, out registration!);

    private static void RegisterUnaryDouble(string name, string mathMethod)
        => Register(name, args => Expression.Call(typeof(Math).GetMethod(mathMethod, [typeof(double)])!, ToDouble(args[0])), 1, 1);

    /// <summary>Coerces a numeric (or nullable-numeric) argument to <see cref="double"/> for the Math builtins.</summary>
    private static Expression ToDouble(Expression e)
    {
        var underlying = Nullable.GetUnderlyingType(e.Type);
        if (underlying is not null)
            return Expression.Convert(Expression.Coalesce(e, Expression.Default(underlying)), typeof(double));
        return e.Type == typeof(double) ? e : Expression.Convert(e, typeof(double));
    }
}
