using UnityEngine;

/// <summary>
/// Put next to an Animator whose movement is driven by a CharacterController. Handling OnAnimatorMove makes
/// Unity hand root motion to this script instead of baking it into the pose; it is then dropped. Clips that
/// leave motion unbaked (e.g. Jump_Air's own hop) therefore cannot fight the physics.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
public sealed class DiscardRootMotion : MonoBehaviour
{
    private void OnAnimatorMove() { }
}
