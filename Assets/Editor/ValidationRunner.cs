using UnityEngine;
using UnityEditor;

public class ValidationRunner : MonoBehaviour {
    private int frames = 0;
    private GameObject player;
    private Transform huTao;
    private Animator animator;

    void Start() {
        Debug.Log("[VALIDATION] Start");
        player = GameObject.Find("Player");
        if (player) {
            huTao = player.transform.Find("Hu tao");
            if (huTao) animator = huTao.GetComponent<Animator>();
        }
    }

    void Update() {
        frames++;

        if (frames == 30) {
            if (huTao) Debug.Log($"[VALIDATION_IDLE] Tilt: X={huTao.eulerAngles.x:F3}, Z={huTao.eulerAngles.z:F3}");
            if (animator) animator.SetFloat("Speed", 6.5f);
        }
        
        if (frames == 90) {
            if (huTao) Debug.Log($"[VALIDATION_RUNNING] Tilt: X={huTao.eulerAngles.x:F3}, Z={huTao.eulerAngles.z:F3}");
            if (animator) animator.SetTrigger("Jump");
        }

        if (frames == 110) {
            if (animator) {
                var state = animator.GetCurrentAnimatorStateInfo(0);
                var nextState = animator.GetNextAnimatorStateInfo(0);
                Debug.Log($"[VALIDATION_JUMP] Current: {state.IsName("Jump")}, Next: {nextState.IsName("Jump")}");
            }
        }

        if (frames >= 120) {
            Debug.Log("[VALIDATION_COMPLETE]");
            EditorApplication.isPlaying = false;
        }
    }
}
