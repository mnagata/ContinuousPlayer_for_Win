using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace ContinuousPlayer;

public sealed class CursorSurface : Control
{
    public void SetCursor(InputCursor? cursor) => ProtectedCursor = cursor;
}
