// Wrist-mounted VR configuration panel.
// Initial stage: create, position, hide, and destroy the panel.

using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;

namespace WetReality.XRStart;

public sealed partial class XRStart
{
    private GameObject? vrConfigHolder;
    private RectTransform? vrConfigRect;
    private CanvasGroup? vrConfigGroup;
    private bool vrConfigOpen;

    private const float VrConfigWidth = 300f;
    private const float VrConfigHeight = 210f;

    private void EnsureVrConfigMenu()
    {
        if (vrConfigHolder != null) return;

        try
        {
            vrConfigHolder = new GameObject("WetReality_VR_Config");
            UnityEngine.Object.DontDestroyOnLoad(vrConfigHolder);
            vrConfigHolder.layer = 5;

            var canvas = vrConfigHolder.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 32100;

            vrConfigRect = vrConfigHolder.GetComponent<RectTransform>();
            if (vrConfigRect == null) return;

            vrConfigRect.sizeDelta = new Vector2(VrConfigWidth, VrConfigHeight);
            vrConfigRect.localScale = Vector3.one * 0.0006f;

            vrConfigGroup = vrConfigHolder.AddComponent<CanvasGroup>();
            vrConfigGroup.alpha = 1f;
            vrConfigGroup.interactable = false;
            vrConfigGroup.blocksRaycasts = false;

            var background = new GameObject("Background");
            background.layer = 5;
            background.transform.SetParent(vrConfigHolder.transform, false);

            var image = background.AddComponent<Image>();
            image.color = new Color(0.015f, 0.025f, 0.02f, 1f);
            image.raycastTarget = false;

            var bgRect = background.GetComponent<RectTransform>();
            if (bgRect != null)
            {
                bgRect.anchorMin = Vector2.zero;
                bgRect.anchorMax = Vector2.one;
                bgRect.offsetMin = Vector2.zero;
                bgRect.offsetMax = Vector2.zero;
            }

            var raycaster = vrConfigHolder.AddComponent<GraphicRaycaster>();

            vrConfigHolder.SetActive(false);
            LoggerInstance.Msg("VR CONFIG: wrist panel created");
        }
        catch (System.Exception e)
        {
            LoggerInstance.Warning("VR CONFIG: create failed: " + e.Message);
            DestroyVrConfigMenu();
        }
    }

    private void DriveVrConfigMenu()
    {
        if (!vrConfigOpen)
        {
            if (vrConfigHolder != null && vrConfigHolder.activeSelf)
                vrConfigHolder.SetActive(false);
            return;
        }
        if (!started || !writeHead)
        {
            if (vrConfigHolder != null && vrConfigHolder.activeSelf)
                vrConfigHolder.SetActive(false);
            return;
        }

        if (vrConfigHolder == null) return;

        try
        {
            var hmd = InputSystem.GetDevice<XRHMD>();
            var controller = OffCtl;
            var cam = Camera.main;

            if (hmd == null || controller == null || cam == null ||
                !hmd.isTracked.isPressed || !controller.isTracked.isPressed)
            {
                if (vrConfigHolder.activeSelf) vrConfigHolder.SetActive(false);
                return;
            }

            var hmdPos = hmd.centerEyePosition.ReadValue();
            var hmdRot = hmd.centerEyeRotation.ReadValue();
            var camT = cam.transform;
            var toWorld = camT.rotation * Quaternion.Inverse(hmdRot);

            var grip = controller.devicePosition.ReadValue();
            var handRot = controller.deviceRotation.ReadValue();

            var world = camT.position + toWorld * (grip - hmdPos);

            // Match the visible free-hand model's rotation correction.
            var freeHandRotation = leftHanded
                ? MirrorRot(handLRot)
                : handLRot;

            var rotation = toWorld * handRot * Quaternion.Euler(freeHandRotation);

            // Put the display on the back of the hand, facing the player.
            var panelRotation = rotation * Quaternion.Euler(0f, 0f, 0f);
            var panelPosition = world + rotation * new Vector3(
                leftHanded ? 0.04f : -0.04f,
                0.025f,
                -0.08f);

            if (!vrConfigHolder.activeSelf)
                vrConfigHolder.SetActive(true);

            vrConfigHolder.transform.SetPositionAndRotation(panelPosition, panelRotation);
        }
        catch (System.Exception e)
        {
            if (vrConfigHolder.activeSelf) vrConfigHolder.SetActive(false);
            LoggerInstance.Warning("VR CONFIG: tracking failed: " + e.Message);
        }
    }

    private void DestroyVrConfigMenu()
    {
        if (vrConfigHolder != null)
        {
            UnityEngine.Object.Destroy(vrConfigHolder);
        }

        vrConfigHolder = null;
        vrConfigRect = null;
        vrConfigGroup = null;
    }
}
