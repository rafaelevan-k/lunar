using Xunit.Sdk;
using Xunit.v3;

// WPF's XAML loader is not safe when several test threads parse XAML at the same time: parallel runs of the
// window tests intermittently failed with "The given key 'Mode' was not present in the dictionary" while loading
// IslandWindow. These tests create real windows, so they run one at a time.
[assembly: Parallelization(Mode = ParallelMode.None)]
