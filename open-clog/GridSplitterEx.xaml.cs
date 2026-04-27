using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;

namespace open_clog
{
    public sealed partial class GridSplitterEx : UserControl
    {
        private InputSystemCursor _horizontalCursor = null;
        private InputSystemCursor _verticalCursor = null;
        private double _pointerOffset = 0;
        private double _originalLength = 0;
        private bool _isActive = false;

        public bool InvertDirection { get; set; } = true;

        public GridSplitterEx()
        {
            this.InitializeComponent();
        }

        bool IsHorizontal => this.ActualWidth > this.ActualHeight;

        Grid GetParentGrid()
        {
            return this.Parent as Grid;
        }

        private void Splitter_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (IsHorizontal)
            {
                _horizontalCursor ??= InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
                ProtectedCursor = _horizontalCursor;
            }
            else
            {
                _verticalCursor ??= InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
                ProtectedCursor = _verticalCursor;
            }
        }

        private void Splitter_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_isActive)
                ProtectedCursor = null;
        }

        private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var grid = GetParentGrid();
            if (grid == null) return;
            if (!_splitterRect.CapturePointer(e.Pointer)) return;

            _isActive = true;
            var currentPosition = e.GetCurrentPoint(grid).Position;
            _pointerOffset = IsHorizontal ? currentPosition.Y : currentPosition.X;
            _originalLength = GetDefinitionAbsoluteLength();
        }

        private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isActive) return;
            var grid = GetParentGrid();
            if (grid == null) return;

            var currentPosition = e.GetCurrentPoint(grid).Position;
            var currentPos = IsHorizontal ? currentPosition.Y : currentPosition.X;
            var delta = currentPos - _pointerOffset;
            var newAbsoluteLength = InvertDirection
                ? _originalLength - delta
                : _originalLength + delta;
            newAbsoluteLength = Math.Max(0, newAbsoluteLength);

            SetDefinitionAbsoluteLength(grid, newAbsoluteLength);
        }

        private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isActive) return;
            _splitterRect.ReleasePointerCapture(e.Pointer);
            _isActive = false;
            ProtectedCursor = null;
        }

        private double GetDefinitionAbsoluteLength()
        {
            var grid = GetParentGrid();
            if (grid == null) return 0;

            int index = IsHorizontal ? Grid.GetRow(this) : Grid.GetColumn(this);
            if (IsHorizontal)
                return grid.RowDefinitions[index]?.ActualHeight ?? 0;
            else
                return grid.ColumnDefinitions[index]?.ActualWidth ?? 0;
        }

        private void SetDefinitionAbsoluteLength(Grid grid, double newLength)
        {
            int index = IsHorizontal ? Grid.GetRow(this) : Grid.GetColumn(this);
            if (IsHorizontal)
                grid.RowDefinitions[index].Height = new GridLength(newLength);
            else
                grid.ColumnDefinitions[index].Width = new GridLength(newLength);
        }
    }
}
