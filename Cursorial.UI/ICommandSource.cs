using System.Windows.Input;

namespace Cursorial.UI
{
    ///<summary>
    /// An interface for classes that know how to invoke a <see cref="ICommand">command</see>.
    ///</summary>
    public interface ICommandSource
    {
        /// <summary>
        /// The command that will be executed when the element is 'invoked'. Classes that implement this
        /// interface should enable or disable by querying a command's 'CanExecute' method.
        /// </summary>
        /// <remarks>
        /// This property may optionally be implemented as read-write.
        /// </remarks>
        ICommand? Command { get; }

        /// <summary>
        /// The parameter that will be passed to the command upon execution.
        /// </summary>
        /// <remarks>
        /// This property may optionally be implemented as read-write.
        /// </remarks>
        object? CommandParameter { get; }

        /// <summary>
        /// Called for the `CanExecuteChanged` event when changes are detected.
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event args.</param>
        void CanExecuteChanged(object sender, EventArgs e);

        /// <summary>
        /// Gets a value indicating whether this control and all its parents are enabled.
        /// </summary>
        bool IsEffectivelyEnabled { get; }
    }
}