using System.Runtime.ExceptionServices;

namespace Cursorial.UI;

/// <summary>
/// Provides utility methods to create and combine <see cref="IDisposable"/> objects.
/// </summary>
internal static class Disposable
{
    /// <summary>
    /// Creates an <see cref="IDisposable"/> instance that invokes the provided dispose action when disposed.
    /// </summary>
    /// <param name="disposeAction">
    /// The action to execute when the returned <see cref="IDisposable"/> instance is disposed.
    /// </param>
    /// <returns>
    /// An <see cref="IDisposable"/> instance that calls the specified <paramref name="disposeAction"/> upon disposal.
    /// </returns>
    public static IDisposable Create(Action disposeAction)
    {
        return new AnonymousDisposable(disposeAction);
    }

    /// <summary>
    /// Combines multiple <see cref="IDisposable"/> instances into a single
    /// <see cref="IDisposable"/>. When the returned object is disposed, it
    /// disposes all aggregated <see cref="IDisposable"/> instances.
    /// </summary>
    /// <param name="ds">
    /// An array of <see cref="IDisposable"/> instances to combine. Elements in
    /// the array can be null, which will be safely ignored during disposal.
    /// </param>
    /// <returns>
    /// An <see cref="IDisposable"/> that, when disposed, disposes all the
    /// provided <see cref="IDisposable"/> instances.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown if the <paramref name="ds"/> parameter is null.
    /// </exception>
    /// <exception cref="AggregateException">
    /// Thrown if one or more exceptions occur during the disposal of the
    /// aggregated disposables. Contains all exceptions that were thrown.
    /// </exception>
    public static IDisposable Combine(params IDisposable[] ds)
    {
        return new CompositeDisposable(ds);
    }

    #region AnonymousDisposable Class

    /// <summary>
    /// Represents a disposable resource that executes a user-defined action upon disposal.
    /// </summary>
    private sealed class AnonymousDisposable : IDisposable
    {
        /// <summary>
        /// Represents an action to be performed during the disposal of the object.
        /// This is invoked when the <see cref="Dispose"/> method is called, ensuring
        /// cleanup logic is executed only once.
        /// </summary>
        /// <remarks>
        /// The action is set during the construction of the object and is cleared
        /// after it is executed to prevent multiple invocations.
        /// </remarks>
        private Action? _disposeAction;

        /// <summary>
        /// Represents a disposable resource that executes a provided action upon disposal.
        /// </summary>
        /// <remarks>
        /// This class is used to encapsulate an action for disposal logic. Once disposed,
        /// the encapsulated action is invoked, and the reference to it is cleared to prevent
        /// further invocations. This ensures that the disposable resource can only be disposed once.
        /// </remarks>
        public AnonymousDisposable(Action disposeAction)
        {
            _disposeAction = disposeAction ?? throw new ArgumentNullException(nameof(disposeAction));
        }

        /// Releases the resources used by the current instance of the class.
        /// This method is part of the IDisposable pattern and should be called
        /// when the instance is no longer needed to ensure any associated resources
        /// are properly disposed of.
        /// Implementations of this method should ensure that disposable resources
        /// are released only once and must guard against multiple calls. Any custom
        /// cleanup logic should be encapsulated within this method.
        public void Dispose()
        {
            var action = Interlocked.Exchange(ref _disposeAction, null);
            action?.Invoke();
        }
    }

    #endregion

    #region CompositeDisposable Class

    /// <summary>
    /// Represents a composite of multiple <see cref="IDisposable"/> objects that are disposed together.
    /// </summary>
    /// <remarks>
    /// The <c>CompositeDisposable</c> class provides a mechanism to manage multiple disposable resources
    /// by grouping them. When the <c>Dispose</c> method of this class is called, all the disposable objects
    /// in the group are disposed in sequence. Any exceptions that occur during the disposal of individual
    /// objects will be collected and re-thrown as an <see cref="AggregateException"/>.
    /// </remarks>
    private sealed class CompositeDisposable : IDisposable
    {
        /// <summary>
        /// Represents a collection of disposable objects managed collectively.
        /// Ensures that all associated disposable objects are properly disposed when
        /// the containing object is disposed.
        /// </summary>
        private IDisposable?[]? _disposables;

        /// <summary>
        /// Represents a collection of <see cref="IDisposable"/> objects that are disposed together.
        /// </summary>
        /// <remarks>
        /// This class allows for managing multiple disposable resources as a single unit. When the instance of
        /// <see cref="CompositeDisposable"/> is disposed, it ensures that all contained <see cref="IDisposable"/> instances
        /// are disposed. Any exceptions thrown during the disposal of these objects are collected and rethrown as a single
        /// <see cref="AggregateException"/> if multiple exceptions occur.
        /// </remarks>
        public CompositeDisposable(IDisposable?[] disposables)
        {
            _disposables = disposables ?? throw new ArgumentNullException(nameof(disposables));
        }

        /// Disposes of the resources held by the object.
        /// Implements the IDisposable pattern, releasing any associated resources or executing custom cleanup logic.
        /// This method ensures that the object's state is cleanly finalized.
        /// In the case of the CompositeDisposable implementation,
        /// it disposes of all associated disposables and aggregates exceptions if any occur during the process.
        /// In the AnonymousDisposable implementation, it invokes the provided dispose action exactly once.
        /// Throws:
        /// - AggregateException: If one or more exceptions occur during the disposal of a CompositeDisposable.
        /// - Exception: Any exception encountered during the execution of a provided dispose action.
        public void Dispose()
        {
            var disposables = Interlocked.Exchange(ref _disposables, null);
            if (disposables == null) return;

            List<Exception>? exceptions = null;

            foreach (var disposable in disposables)
            {
                try
                {
                    disposable?.Dispose();
                }
                catch (Exception ex)
                {
                    exceptions ??= [];
                    exceptions.Add(ex);
                }
            }

            if (exceptions != null)
            {
                if (exceptions.Count == 1)
                    ExceptionDispatchInfo.Capture(exceptions[0]).Throw();

                throw new AggregateException("One or more exceptions occurred during disposal.", exceptions);
            }
        }
    }

    #endregion
}