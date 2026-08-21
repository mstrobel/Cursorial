using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;

using Cursorial.Gallery.Infrastructure;
using Cursorial.UI;

namespace Cursorial.Gallery.ViewModels;

/// <summary>
/// The DataGrid page (the DataViews live canary): a few hundred orders behind a toggleable ticking
/// feed — sorting/grouping/filtering/editing ride user gestures against live data (the design doc's
/// headline scenario at gallery scale).
/// </summary>
public class DataGridViewModel : PageViewModel
{
    private const int Capacity = 300;
    
    public override string Title => "Data Grid";
    public override string Summary => "Sortable, groupable, live-shaping DataGrid over the DataViews engine. " +
                                      "Click headers to sort (Ctrl+click adds levels); " +
                                      "right-click rows for the command menu; " +
                                      "F6 walks the bands (header: ⏎ sorts, ␣ adds a level, ^G groups, ⌥↓ filters); " +
                                      "F2/⏎ edits.";

    public override bool IsContentScrollable => false;

    public enum OrderStatus { Shipped, Processing, [Display(Name = "On Hold")] OnHold, Canceled };

    private static readonly string[] Regions = ["East", "West", "North", "South"];
    private static readonly string[] Reps = ["A. Chen", "K. Brooks", "M. Ortiz", "S. Kim", "R. Patel"];
    private static readonly OrderStatus[] OrderStatuses = Enum.GetValues<OrderStatus>();

    private readonly Random _random = new(2026);
    private UITimer? _feed;
    private bool _feedRunning;

    // DynamicDependency, not just the type-level DAM below: on this toolchain the type-level
    // attribute engages only for REFLECTION-VISIBLE type references, and the grid discovers the row
    // type through a dataflow-opaque Type variable — so ILC never triggered it and GetProperties()
    // came back empty (the blank-grid deficit, round two). DynamicDependency preserves the members
    // unconditionally as long as this ctor is kept.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(OrderRow))]
    public DataGridViewModel()
    {
        // AOT: statically close the shaping engine's generics over OrderRow (see DataGridAotSupport).
        Cursorial.UI.DataViews.DataGridAotSupport.Seed<OrderRow>();
        Cursorial.UI.DataViews.DataGridAotSupport.Seed<OrderRow, OrderStatus>();

        var rows = new List<OrderRow>(capacity: Capacity);
        for (int i = 0; i < Capacity; i++)
        {
            rows.Add(new OrderRow
            {
                Order = $"SO-{1000 + i}",
                Region = Regions[_random.Next(Regions.Length)],
                Rep = Reps[_random.Next(Reps.Length)],
                Amount = _random.Next(1_000, 50_000),
                Margin = Math.Round(_random.NextDouble() * 0.5, 2),
                OrderStatus = OrderStatuses[_random.Next(OrderStatuses.Length)],
            });
        }
        Orders = new ObservableCollection<OrderRow>(rows);

        ToggleFeed = new RelayCommand(ExecuteToggleFeed);
    }

    /// <summary>The grid's source (live INPC rows; INCC adds/removes when the feed runs).</summary>
    public ObservableCollection<OrderRow> Orders { get; }

    /// <summary>Starts/stops the ticking feed (a frame-aligned UITimer mutating a few rows per tick).</summary>
    public RelayCommand ToggleFeed { get; }

    /// <summary>The feed toggle's label (bound by the page view).</summary>
    public string FeedLabel => _feedRunning ? "Stop feed" : "Start feed";

    private void ExecuteToggleFeed()
    {
        if (_feedRunning)
        {
            _feed?.Stop();
            _feedRunning = false;
        }
        else
        {
            _feed?.Dispose();
            _feed = UITimer.Start(TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(120), Tick);
            _feedRunning = true;
        }
        Raise(nameof(FeedLabel));
    }

    private void Tick()
    {
        // A small burst of value changes per tick — the incremental-repair path's live profile.
        for (int i = 0; i < Capacity / 20; i++)
        {
            var row = Orders[_random.Next(Orders.Count)];
            row.Amount = Math.Max(500, row.Amount + _random.Next(-4_000, 4_500));
        }

        // Occasionally churn membership (insert/remove — the INCC lanes).
        if (_random.Next(10) == 0)
        {
            if (Orders.Count > (Capacity - 1) / 50 * 50 && _random.Next(2) == 0)
            {
                Orders.RemoveAt(_random.Next(Orders.Count));
            }
            else
            {
                Orders.Add(new OrderRow
                {
                    Order = $"SO-{9000 + _random.Next(999)}",
                    Region = Regions[_random.Next(Regions.Length)],
                    Rep = Reps[_random.Next(Reps.Length)],
                    Amount = _random.Next(1_000, 50_000),
                    Margin = Math.Round(_random.NextDouble() * 0.5, 2),
                    OrderStatus = OrderStatuses[_random.Next(OrderStatuses.Length)],
                });
            }
        }
    }

    /// <summary>One order (INPC — the live-update contract; Amount/Status are editable).</summary>
    /// <remarks>The grid AUTO-GENERATES its columns by reflecting over this type — under trim/AOT
    /// nothing else references these getters, so without the DAM root the trimmer strips them and
    /// the grid renders header-less blank rows (the Gallery AOT sweep's empty-grid deficit).</remarks>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    public sealed class OrderRow : INotifyPropertyChanged
    {
        public required string Order { get; init => Set(ref field, value); }
        public required string Region { get; init => Set(ref field, value); }

        public required string Rep { get; init => Set(ref field, value); }

        public decimal Amount { get => field; set => Set(ref field, value); }
        public double Margin { get; init => Set(ref field, value); }
        public OrderStatus OrderStatus { get => field; set => Set(ref field, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        }
    }
}
