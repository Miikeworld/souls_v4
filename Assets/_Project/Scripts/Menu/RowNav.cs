using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Sits on a character-creator option row: left/right cycles the row's value,
/// selecting the row shows its description. Vertical navigation is left to the
/// Selectable on the same object (we only consume horizontal moves).
/// </summary>
public sealed class RowNav : MonoBehaviour, IMoveHandler, ISelectHandler
{
    public CharacterCreatorController.Row row;
    public CharacterCreatorController owner;

    public void OnMove(AxisEventData e)
    {
        if (e.moveDir == MoveDirection.Left && row.isEnabled()) { row.cycle(-1); e.Use(); }
        else if (e.moveDir == MoveDirection.Right && row.isEnabled()) { row.cycle(1); e.Use(); }
    }

    public void OnSelect(BaseEventData e) => owner.ShowInfo(row.describe());
}
