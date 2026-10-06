using UnityEngine;

/// <summary>
/// Lives on the gameplay player. The katana is always the starting weapon.
/// When a saved character build exists it is applied at scene load —
/// appearance toggles and colors in Awake (before the first frame renders),
/// stat pools in Start (after every other component's Awake has refilled its
/// defaults). With no save file the scene's serialized pools run untouched.
/// </summary>
public sealed class PlayerCustomizer : MonoBehaviour
{
    [Tooltip("The starting (and only) weapon.")]
    [SerializeField] private WeaponSet katanaSet;
    [Tooltip("Keep saved stats, but disable Fantasy Hero appearance application when using the authored protagonist.")]
    [SerializeField] private bool applySavedAppearance = true;

    private CharacterBuildData build;

    private void Awake()
    {
        if (!CharacterBuildData.HasSave) return;
        build = CharacterBuildData.Load();

        // The Fantasy Hero instance lives under the player root — apply from
        // the Animator's transform so the search covers every Chr_* part.
        var anim = GetComponentInChildren<Animator>(true);
        if (applySavedAppearance)
            HeroLibrary.Apply(anim != null ? anim.transform : transform, build);
    }

    private void Start()
    {
        var socket = GetComponent<WeaponSocket>();
        if (socket != null && katanaSet != null) socket.Equip(katanaSet);
        if (build == null) return;

        var hp = GetComponent<PlayerHealth>();
        if (hp != null) hp.ConfigureMax(CharacterCatalog.StatRules.Hp(build.vigor));
        var sp = GetComponent<PlayerStamina>();
        if (sp != null) sp.ConfigureMax(CharacterCatalog.StatRules.Stamina(build.endurance));
        var mp = GetComponent<PlayerMana>();
        if (mp != null) mp.ConfigureMax(CharacterCatalog.StatRules.Mana(build.mind));

        Debug.Log($"[PlayerCustomizer] '{build.characterName}' — VIG {build.vigor} END {build.endurance} MND {build.mind}, " +
                  $"{(build.female ? "F" : "M")}, weapon {(katanaSet != null ? katanaSet.displayName : "default")}.", this);
    }
}
