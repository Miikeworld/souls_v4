using System.Collections.Generic;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// GraphicRaycaster for a <see cref="PixelCanvas"/>: the canvas camera renders
/// into a small RenderTexture, so its pixel space is not the screen's. Pointer
/// positions are scaled into render-texture pixels for the hit test, then
/// restored — hover, click, drag and keyboard navigation all keep working.
/// </summary>
public sealed class PixelRaycaster : GraphicRaycaster
{
    private PixelCanvas pixelCanvas;

    public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
    {
        if (pixelCanvas == null) pixelCanvas = GetComponentInParent<PixelCanvas>();
        if (pixelCanvas == null)
        {
            base.Raycast(eventData, resultAppendList);
            return;
        }
        var original = eventData.position;
        eventData.position = pixelCanvas.ScreenToPixel(original);
        base.Raycast(eventData, resultAppendList);
        eventData.position = original;
    }
}
