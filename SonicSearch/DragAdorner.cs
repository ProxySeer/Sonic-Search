using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SonicSearch
{
    /// <summary>
    /// Draws a static bitmap "ghost" of a dragged tile that follows the cursor, rendered inside
    /// the AdornerLayer of the window's own content. Unlike a Popup, an Adorner has no HWND of
    /// its own: moving it is just an InvalidateVisual/repaint within the existing window, so it
    /// (a) never intercepts OLE drag-and-drop target routing - which is resolved by the Win32
    /// window under the cursor, and a covering Popup's own layered window steals that from the
    /// real window underneath, breaking Drop entirely - and (b) has none of the per-frame
    /// SetWindowPos cost that made the Popup-based preview feel laggy.
    /// </summary>
    public class DragAdorner : Adorner
    {
        private readonly ImageSource _image;
        private readonly System.Windows.Size _size;
        private Point _position;

        public DragAdorner(UIElement adornedElement, ImageSource image, System.Windows.Size size, Point initialPosition)
            : base(adornedElement)
        {
            _image = image;
            _size = size;
            IsHitTestVisible = false;
            _position = initialPosition;
        }

        public void UpdatePosition(Point centerPosition)
        {
            _position = centerPosition;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var topLeft = new Point(_position.X - _size.Width / 2, _position.Y - _size.Height / 2);
            drawingContext.PushOpacity(0.8);
            drawingContext.DrawImage(_image, new Rect(topLeft, _size));
            drawingContext.Pop();
        }
    }
}
