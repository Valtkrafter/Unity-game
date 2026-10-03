using System.Threading.Tasks;
using UnityEngine;
using VRM;

/// <summary>
/// Moves this VRM 0.x model's spring bones (hair, skirt, bust) from one MonoBehaviour update per chain group to
/// UniVRM's FastSpringBone runtime: every chain batched into a single Burst-compiled job. Same settings, same look,
/// a fraction of the main-thread cost. Runs after everything else in LateUpdate (FastSpringBoneService, order 11000),
/// so ArmClothClearance's corrected arms are what the hair collides with.
/// </summary>
[DisallowMultipleComponent]
public sealed class FastSpringBoneActivator : MonoBehaviour
{
    private void Awake()
    {
        var runtime = new Vrm0XFastSpringboneRuntime();
        // InitializeAsync(GameObject, IAwaitCaller): IAwaitCaller lives in UniGLTF.Utils, which isn't auto-referenced
        // by game scripts, hence the reflection call. A null caller makes UniVRM build the buffers synchronously.
        var task = (Task)typeof(Vrm0XFastSpringboneRuntime).GetMethod(nameof(Vrm0XFastSpringboneRuntime.InitializeAsync))
            .Invoke(runtime, new object[] { gameObject, null });
        if (task.IsFaulted) Debug.LogException(task.Exception, this);
    }
}
