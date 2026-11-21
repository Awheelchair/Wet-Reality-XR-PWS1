
// Die VR-Haende des Spiels an den Controllern (XRStart 1.11.0), Stufe 1:
// sichtbar und am richtigen Ort, noch ohne Fingerposen. Port von PWS2 VrHands.
//
// SCHLUESSEL GEMESSEN (catalog.json, m_KeyDataString entschluesselt, 186570
// Schluessel) - NICHT die PWS2-Pfade:
//   PWS2  Assets/PWS/Content/Core/FBX/Oculus/Rig_VRHand_L.fbx
//   PWS1  Assets/PWS/Content/Core/FBX/Player/Rig_VRHand_L.fbx
//
// Laden wie PWS2: Addressables.InstantiateAsync, gepollt (kein await im Melon).
// Halter DontDestroyOnLoad, ueberlebt Levelwechsel.
//
// RECHTES RIG PUNKTGESPIEGELT: Material der rechten Hand beidseitig (_Cull 0).
//
// SPIELARME: Renderer unter Camera.main werden ausgeblendet, wenn sie nicht
// unter dem Pistolenanker haengen. Zurueck, wenn die VR-Haende verschwinden.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;

namespace WetReality.XRStart;

public sealed partial class XRStart
{
    private const string HandKeyL =
        "Assets/PWS/Content/Core/FBX/Player/Rig_VRHand_L.fbx";

    private const string HandKeyR =
        "Assets/PWS/Content/Core/FBX/Player/Rig_VRHand_R.fbx";

    private GameObject? handHolder, handL, handR;

    private UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject>? handHandle;

    private string? appliedGlovesId;

    private UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<Material>? gloveMaterialHandle;

    private bool gloveMaterialLoading;
    private float nextGloveCheck;

    private int handStep;
    private float handDeadline;

    private readonly List<Renderer> armsHidden = new();

    private Vector3 handRPos, handRRot, handLPos, handLRot;

    private float nextArmsCheck;
    private float nextVisualDiagnostic;


    private void DiagnoseCharacterVisuals()
    {
        if (Time.unscaledTime < nextVisualDiagnostic) return;
        nextVisualDiagnostic = Time.unscaledTime + 5f;

        try
        {
            var player =
                UnityEngine.Object.FindObjectOfType<Il2CppPWS.PlayerCharacter>();

            LoggerInstance.Msg(
                $"BODY CHECK: PlayerCharacter={(player == null ? "MISSING" : player.gameObject.name)}");

            if (player == null) return;

            var first = player.FirstPersonVisuals;
            var third = player.ThirdPersonVisuals;

            LoggerInstance.Msg(
                $"BODY CHECK: FirstPersonVisuals={(first == null ? "MISSING" : first.gameObject.name)}");

            LoggerInstance.Msg(
                $"BODY CHECK: ThirdPersonVisuals={(third == null ? "MISSING" : third.gameObject.name)}");

            if (first != null)
            {
                LoggerInstance.Msg(
                    $"BODY CHECK: FIRST root active={first.gameObject.activeSelf}, hierarchy={first.gameObject.activeInHierarchy}");

                foreach (var r in first.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;

                    LoggerInstance.Msg(
                        $"BODY CHECK: FIRST renderer '{PathOf(r.transform)}' enabled={r.enabled}, active={r.gameObject.activeInHierarchy}");
                }
            }

            if (third != null)
            {
                LoggerInstance.Msg(
                    $"BODY CHECK: THIRD root active={third.gameObject.activeSelf}, hierarchy={third.gameObject.activeInHierarchy}");
                var thirdPerson = third.TryCast<Il2CppPWS.CharacterVisualsThirdPerson>();

                if (thirdPerson != null)
                {
                    var body = thirdPerson.m_bodyOutfitRenderer;
                    var head = thirdPerson.m_headOutfitRenderer;

                    LoggerInstance.Msg(
                        $"BODY CHECK: OUTFIT BODY={(body == null ? "MISSING" : body.name)}" +
                        (body == null ? "" : $" enabled={body.enabled}, active={body.gameObject.activeInHierarchy}, layer={body.gameObject.layer}, forceOff={body.forceRenderingOff}"));
                    if (body != null)
                    {
                        var mat = body.sharedMaterial;

                        LoggerInstance.Msg(
                            $"BODY CHECK: BODY MATERIAL={(mat == null ? "MISSING" : mat.name)}" +
                            (mat == null || mat.shader == null ? "" : $" shader={mat.shader.name}"));

                        var cam = Camera.main;

                        if (cam != null)
                        {
                            LoggerInstance.Msg(
                                $"BODY CHECK: CAMERA={cam.name} cullingMask={cam.cullingMask} " +
                                $"seesBodyLayer={(cam.cullingMask & (1 << body.gameObject.layer)) != 0}");
                        }
                    }

                    LoggerInstance.Msg(
                        $"BODY CHECK: OUTFIT HEAD={(head == null ? "MISSING" : head.name)}" +
                        (head == null ? "" : $" enabled={head.enabled}, active={head.gameObject.activeInHierarchy}, layer={head.gameObject.layer}, forceOff={head.forceRenderingOff}"));
                }
                else
                {
                    LoggerInstance.Warning("BODY CHECK: ThirdPersonVisuals cast failed");
                }

                foreach (var r in third.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;

                    LoggerInstance.Msg(
                        $"BODY CHECK: THIRD renderer '{PathOf(r.transform)}' enabled={r.enabled}, active={r.gameObject.activeInHierarchy}");
                }
            }
        }
        catch (Exception e)
        {
            LoggerInstance.Warning("BODY CHECK: " + e);
        }
    }


    // "x,y,z" aus der cfg; bei Unlesbarem die PWS2-Vorgabe.
    private Vector3 ParseVec(string text, Vector3 fallback, string name)
    {
        try
        {
            var p = text.Split(',');
            var ci = System.Globalization.CultureInfo.InvariantCulture;

            if (p.Length == 3)
            {
                return new Vector3(
                    float.Parse(p[0].Trim(), ci),
                    float.Parse(p[1].Trim(), ci),
                    float.Parse(p[2].Trim(), ci));
            }
        }
        catch { }

        LoggerInstance.Warning(
            $"HAENDE: {name} '{text}' unlesbar - Vorgabe {fallback.ToString("F2")}");

        return fallback;
    }

    // Liest den aktuell ausgeruesteten Handschuh und laedt dessen Material.
    private void UpdateEquippedVrGloves()
    {
        if (handL == null || handR == null) return;

        float now = Time.unscaledTime;

        if (now < nextGloveCheck) return;

        nextGloveCheck = now + 0.5f;

        try
        {
            var player =
                UnityEngine.Object.FindObjectOfType<Il2CppPWS.PlayerCharacter>();

            if (player == null) return;

            var cosmetics = player.CosmeticsManager;
            if (cosmetics == null) return;

            var gloves = cosmetics.Gloves;
            if (gloves == null) return;

            string id = gloves.UniqueId.ToString();

            if (id.Length == 0 ||
                id == appliedGlovesId ||
                gloveMaterialLoading)
            {
                return;
            }

            var asset = gloves.GlovesMaterialAsset;

            if (asset == null)
            {
                LoggerInstance.Warning(
                    $"HAENDE: Handschuh '{id}' hat keine GlovesMaterialAsset");
                return;
            }

            var key = asset.RuntimeKey;

            if (key == null)
            {
                LoggerInstance.Warning(
                    $"HAENDE: GlovesMaterialAsset '{id}' hat keinen RuntimeKey");
                return;
            }

            gloveMaterialLoading = true;

            Il2CppSystem.Object k = key;

            var handle =
                UnityEngine.AddressableAssets.Addressables
                    .LoadAssetAsync<Material>(k);

            gloveMaterialHandle = handle;

            LoggerInstance.Msg(
                $"HAENDE: lade ausgeruesteten Handschuh '{id}'");
        }
        catch (Exception e)
        {
            gloveMaterialLoading = false;

            LoggerInstance.Warning(
                $"HAENDE: Handschuh-Abfrage {e.GetType().Name}: {e.Message}");
        }
    }

    // Prueft, ob das asynchrone Laden abgeschlossen ist, und wendet das Material an.
    private void FinishEquippedVrGloves()
    {
        if (gloveMaterialHandle == null) return;

        try
        {
            var h = gloveMaterialHandle;

            if (!h.IsDone) return;

            gloveMaterialHandle = null;
            gloveMaterialLoading = false;

            if (h.Status !=
                    UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded ||
                h.Result == null)
            {
                LoggerInstance.Warning(
                    $"HAENDE: Handschuh-Material konnte nicht geladen werden, Status {h.Status}");
                return;
            }

            var source = h.Result;

            ApplyGloveMaterial(handL, source);
            ApplyGloveMaterial(handR, source);

            var player =
                UnityEngine.Object.FindObjectOfType<Il2CppPWS.PlayerCharacter>();

            var cosmetics = player?.CosmeticsManager;
            var gloves = cosmetics?.Gloves;

            appliedGlovesId = gloves?.UniqueId.ToString() ?? "";

            LoggerInstance.Msg(
                $"HAENDE: ausgeruesteter Handschuh angewendet '{appliedGlovesId}' -> Material '{source.name}'");
        }
        catch (Exception e)
        {
            gloveMaterialHandle = null;
            gloveMaterialLoading = false;

            LoggerInstance.Warning(
                $"HAENDE: Handschuh-Material {e.GetType().Name}: {e.Message}");
        }
    }

    // Ersetzt die Materialien der VR-Hand durch das echte Handschuhmaterial.
    private void ApplyGloveMaterial(GameObject? hand, Material source)
    {
        if (hand == null || source == null) return;

        try
        {
            foreach (var r in hand.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;

                var mats = r.materials;

                if (mats == null || mats.Length == 0) continue;

                var glove = UnityEngine.Object.Instantiate(source);

                // Rechter VR-Hand-Rig ist punktgespiegelt.
                // Beidseitiges Zeichnen beibehalten.
                SetIntSafe(glove, "_Cull", 0);

                for (int i = 0; i < mats.Length; i++)
                    mats[i] = glove;

                r.materials = mats;
            }
        }
        catch (Exception e)
        {
            LoggerInstance.Warning(
                $"HAENDE: Material auf '{hand.name}' {e.GetType().Name}: {e.Message}");
        }
    }

    // Aktualisiert Position und Drehung der VR-Haende.
    private void DriveVrHands()
    {
        bool want = prefHands.Value && started && writeHead;

        if (!want)
        {
            if (handHolder != null && handHolder.activeSelf)
            {
                handHolder.SetActive(false);
                ShowArms();
            }

            return;
        }

        if (handStep >= 0 && handStep < 3)
        {
            LoadHands();
            return;
        }

        if (handStep < 0 || handHolder == null) return;

        if (!handHolder.activeSelf)
            handHolder.SetActive(true);

        UpdateEquippedVrGloves();
        FinishEquippedVrGloves();
        DiagnoseCharacterVisuals();
        try
        {
            var hmd = InputSystem.GetDevice<XRHMD>();
            var cam = Camera.main;

            if (hmd == null || cam == null) return;

            var hmdPos = hmd.centerEyePosition.ReadValue();
            var hmdRot = hmd.centerEyeRotation.ReadValue();

            var camT = cam.transform;

            var toWorld =
                camT.rotation * Quaternion.Inverse(hmdRot);

            // Linkshaendig: Pistolenhand links, freie Hand rechts.
            if (!leftHanded)
            {
                PlaceHand(
                    handR,
                    XRController.rightHand,
                    true,
                    camT,
                    toWorld,
                    hmdPos,
                    handRPos,
                    handRRot);

                PlaceHand(
                    handL,
                    XRController.leftHand,
                    false,
                    camT,
                    toWorld,
                    hmdPos,
                    handLPos,
                    handLRot);
            }
            else
            {
                PlaceHand(
                    handL,
                    XRController.leftHand,
                    true,
                    camT,
                    toWorld,
                    hmdPos,
                    MirrorPos(handRPos),
                    MirrorRot(handRRot));

                PlaceHand(
                    handR,
                    XRController.rightHand,
                    false,
                    camT,
                    toWorld,
                    hmdPos,
                    MirrorPos(handLPos),
                    MirrorRot(handLRot));
            }

            float now = Time.unscaledTime;

            if (now >= nextArmsCheck)
            {
                nextArmsCheck = now + 1f;
                HideArms(cam);
            }
        }
        catch (Exception e)
        {
            LoggerInstance.Warning(
                "HAENDE: " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static void PlaceHand(
        GameObject? hand,
        XRController? c,
        bool aimRotation,
        Transform camT,
        Quaternion toWorld,
        Vector3 hmdPos,
        Vector3 posOffset,
        Vector3 rotOffset)
    {
        if (hand == null) return;

        if (c == null || !c.isTracked.isPressed)
        {
            if (hand.activeSelf)
                hand.SetActive(false);

            return;
        }

        if (!hand.activeSelf)
            hand.SetActive(true);

        var grip = c.devicePosition.ReadValue();

        Quaternion rot = c.deviceRotation.ReadValue();

        if (aimRotation)
        {
            var aimCtl =
                c.TryGetChildControl("pointerRotation")
                    ?.TryCast<QuaternionControl>();

            if (aimCtl != null)
                rot = aimCtl.ReadValue();
        }

        var world =
            camT.position + toWorld * (grip - hmdPos);

        var r =
            toWorld * rot * Quaternion.Euler(rotOffset);

        hand.transform.SetPositionAndRotation(
            world + r * posOffset,
            r);
    }

    private void LoadHands()
    {
        try
        {
            if (handHolder == null)
            {
                handHolder = new GameObject("WetReality_VrHands");

                UnityEngine.Object.DontDestroyOnLoad(handHolder);
            }

            if (handHandle == null)
            {
                string key =
                    handStep == 0 || handStep == 1
                        ? HandKeyL
                        : HandKeyR;

                if (handStep == 0)
                    handStep = 1;

                Il2CppSystem.Object k = (Il2CppSystem.String)key;

                handHandle =
                    UnityEngine.AddressableAssets.Addressables
                        .InstantiateAsync(
                            k,
                            handHolder.transform,
                            false,
                            true);

                handDeadline = Time.unscaledTime + 15f;

                LoggerInstance.Msg($"HAENDE: lade '{key}'");

                return;
            }

            var h = handHandle;

            if (!h.IsDone)
            {
                if (Time.unscaledTime > handDeadline)
                {
                    LoggerInstance.Warning(
                        $"HAENDE: nach 15 s nicht fertig, Status {h.Status} - aufgegeben");

                    handStep = -1;
                }

                return;
            }

            if (h.Status !=
                    UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded ||
                h.Result == null)
            {
                LoggerInstance.Warning(
                    $"HAENDE: Laden fehlgeschlagen, Status {h.Status} - " +
                    $"{(h.OperationException == null ? "keine Ausnahme" : h.OperationException.Message)}");

                handStep = -1;
                return;
            }

            var go = h.Result;

            handHandle = null;

            if (handStep == 1)
            {
                handL = go;
                go.name = "WetReality_HandL";

                DescribeHand(go, "links");
                TintHand(go, "links");

                handStep = 2;
            }
            else
            {
                handR = go;
                go.name = "WetReality_HandR";

                DescribeHand(go, "rechts");
                FixMirrored(go);
                TintHand(go, "rechts");

                handStep = 3;

                LoggerInstance.Msg("HAENDE: beide geladen");
            }
        }
        catch (Exception e)
        {
            LoggerInstance.Warning(
                "HAENDE: Laden " + e.GetType().Name + ": " + e.Message);

            handStep = -1;
        }
    }

    private void DescribeHand(GameObject go, string side)
    {
        try
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);

            var sb = new System.Text.StringBuilder();

            foreach (var r in rs)
            {
                if (r == null) continue;

                var smr = r.TryCast<SkinnedMeshRenderer>();

                string mats = "";

                foreach (var m in r.sharedMaterials)
                {
                    mats +=
                        (m == null
                            ? "none"
                            : $"{m.name}/{(m.shader == null ? "?" : m.shader.name)}")
                        + " ";
                }

                sb.Append(
                    $" | '{r.name}' {r.GetIl2CppType().Name} " +
                    $"layer {r.gameObject.layer} " +
                    $"bones {(smr == null ? 0 : smr.bones.Length)} " +
                    $"root '{(smr == null || smr.rootBone == null ? "-" : smr.rootBone.name)}' " +
                    $"rootScale {(smr == null || smr.rootBone == null ? "-" : smr.rootBone.lossyScale.ToString("F2"))} " +
                    $"mats {mats.Trim()}");
            }

            var anim = go.GetComponentInChildren<Animator>(true);

            LoggerInstance.Msg(
                $"HAENDE: {side} '{go.name}' {rs.Length} Renderer, Animator " +
                $"{(anim == null ? "keiner" : (anim.runtimeAnimatorController == null ? "ohne Controller" : "'" + anim.runtimeAnimatorController.name + "'"))}" +
                sb);
        }
        catch (Exception e)
        {
            LoggerInstance.Warning(
                "HAENDE: Beschreibung " + e.GetType().Name);
        }
    }

    // Stufe 1: das punktgespiegelte Rig beidseitig zeichnen.
    private void FixMirrored(GameObject go)
    {
        try
        {
            int n = 0;

            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;

                var mats = r.materials;

                foreach (var m in mats)
                {
                    if (m == null) continue;

                    SetIntSafe(m, "_Cull", 0);
                    n++;
                }
            }

            LoggerInstance.Msg(
                $"HAENDE: rechts {n} Material(ien) beidseitig (_Cull 0) - Rig ist gespiegelt");
        }
        catch (Exception e)
        {
            LoggerInstance.Warning(
                "HAENDE: Spiegelung " + e.GetType().Name);
        }
    }

    // Spielarme: alles unter der Kamera, was nicht die Pistole ist.
    private void HideArms(Camera cam)
    {
        try
        {
            var anchor = gunAnchor;

            if (anchor == null) return;

            var rs = cam.GetComponentsInChildren<Renderer>(true);

            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];

                if (r == null || !r.enabled) continue;

                if (r.transform.IsChildOf(anchor)) continue;

                if (menuLine != null &&
                    r.Pointer == menuLine.Pointer)
                {
                    continue;
                }

                r.enabled = false;

                armsHidden.Add(r);

                LoggerInstance.Msg(
                    $"HAENDE: Spielkoerper ausgeblendet '{PathOf(r.transform)}' ({r.GetIl2CppType().Name})");
            }
        }
        catch (Exception e)
        {
            LoggerInstance.Warning(
                "HAENDE: Arme " + e.GetType().Name + ": " + e.Message);
        }
    }

    private void ShowArms()
    {
        if (armsHidden.Count == 0) return;

        int n = 0;

        foreach (var r in armsHidden)
        {
            try
            {
                if (r != null)
                {
                    r.enabled = true;
                    n++;
                }
            }
            catch { }
        }

        LoggerInstance.Msg(
            $"HAENDE: Spielkoerper wieder sichtbar ({n})");

        armsHidden.Clear();
    }
}
