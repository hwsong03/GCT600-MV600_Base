using System.IO;
using System.Linq;
using System.Text;
using Oculus.Avatar2;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Tutorial 4: builds AvatarQuest (sender) and AvatarWallDisplay (receiver) from AvatarTutorial.
[InitializeOnLoad]
public static class Tutorial4Setup
{
    private const string SourceScene = "Assets/Scenes/AvatarTutorial.unity";
    private const string QuestScene = "Assets/Scenes/AvatarQuest.unity";
    private const string WallScene = "Assets/Scenes/AvatarWallDisplay.unity";
    private const string CameraRigPrefab = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
    private const string TriggerFile = "Temp/tutorial4_setup.trigger";
    private const string ReportFile = "Temp/tutorial4_setup.log";

    // IP of the PC running server_pose.py and avatar_relay.py, as seen from the Quest.
    public static string ServerIp = "10.249.214.32";

    static Tutorial4Setup()
    {
        // Quest app talks raw TCP to the relay/pose servers, so it needs INTERNET permission.
        if (!PlayerSettings.Android.forceInternetPermission)
        {
            PlayerSettings.Android.forceInternetPermission = true;
            AssetDatabase.SaveAssets();
        }
        if (!File.Exists(TriggerFile)) return;
        ServerIp = File.ReadAllText(TriggerFile).Trim();
        File.Delete(TriggerFile);
        EditorApplication.delayCall += Run;
    }

    [MenuItem("GCT600/Tutorial 4/Create AvatarQuest + AvatarWallDisplay")]
    private static void Run()
    {
        var log = new StringBuilder();
        try
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (s.isDirty)
                {
                    log.AppendLine("ABORT: open scene has unsaved changes: " + s.path);
                    return;
                }
            }

            foreach (string path in new[] { QuestScene, WallScene })
                if (!File.Exists(path) && !AssetDatabase.CopyAsset(SourceScene, path))
                    log.AppendLine("Failed to copy " + path);
            AssetDatabase.Refresh();

            SetupQuestScene(log);
            SetupWallScene(log);

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene("Assets/Scenes/ClientScene.unity", false),
                new EditorBuildSettingsScene(SourceScene, false),
                new EditorBuildSettingsScene(WallScene, false),
                new EditorBuildSettingsScene(QuestScene, true),
            };
            AssetDatabase.SaveAssets();
            log.AppendLine("Build scene list: only AvatarQuest enabled.");
            log.AppendLine("DONE");
        }
        catch (System.Exception ex)
        {
            log.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            File.WriteAllText(ReportFile, log.ToString());
            Debug.Log("[Tutorial4Setup]\n" + log);
        }
    }

    private static void SetupQuestScene(StringBuilder log)
    {
        Scene scene = EditorSceneManager.OpenScene(QuestScene, OpenSceneMode.Single);
        OvrAvatarEntity avatar = FindAvatar(scene);

        GameObject rig = Find(scene, "[BuildingBlock] Camera Rig") ?? FindRigByComponent(scene);
        if (rig == null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CameraRigPrefab);
            rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            rig.name = "[BuildingBlock] Camera Rig";
            log.AppendLine("Quest: added OVRCameraRig");
        }
        rig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        avatar.gameObject.name = "LocalAvatar";
        avatar.transform.SetParent(rig.transform, false);
        avatar.transform.localPosition = Vector3.zero;
        avatar.transform.localRotation = Quaternion.identity;

        // The Quest view comes from the rig, so the standalone camera goes away.
        GameObject mainCam = Find(scene, "Main Camera");
        if (mainCam != null && mainCam.transform.parent == null) Object.DestroyImmediate(mainCam);

        // Drive the local avatar from the headset/controllers.
        var input = avatar.GetComponent<SampleInputManager>() ?? avatar.gameObject.AddComponent<SampleInputManager>();
        var inputSo = new SerializedObject(input);
        inputSo.FindProperty("_ovrCameraRig").objectReferenceValue = rig.GetComponent<OVRCameraRig>();
        inputSo.ApplyModifiedPropertiesWithoutUndo();
        var avatarSo = new SerializedObject(avatar);
        avatarSo.FindProperty("_isLocal").boolValue = true;
        avatarSo.FindProperty("_inputManager").objectReferenceValue = input;
        avatarSo.ApplyModifiedPropertiesWithoutUndo();

        var sender = avatar.GetComponent<MetaAvatarSender>() ?? avatar.gameObject.AddComponent<MetaAvatarSender>();
        sender.avatar = avatar;
        sender.serverAddress = ServerIp;
        sender.serverPort = 5060;
        sender.channel = "quest-user";

        SetStreamManagerIp(scene, ServerIp, log);
        EditorSceneManager.SaveScene(scene);
        log.AppendLine("Quest: " + Describe(scene));
    }

    private static void SetupWallScene(StringBuilder log)
    {
        Scene scene = EditorSceneManager.OpenScene(WallScene, OpenSceneMode.Single);
        OvrAvatarEntity avatar = FindAvatar(scene);
        avatar.transform.SetParent(null, true);
        avatar.gameObject.name = "RemoteAvatar";
        avatar.transform.SetPositionAndRotation(new Vector3(0, 0, -1.05f), Quaternion.identity);

        GameObject rig = Find(scene, "[BuildingBlock] Camera Rig") ?? FindRigByComponent(scene);
        if (rig != null) Object.DestroyImmediate(rig);
        foreach (GameObject root in scene.GetRootGameObjects())
            if (root.GetComponentInChildren<MixamoRetargeter>(true) != null) Object.DestroyImmediate(root);

        GameObject cam = Find(scene, "Main Camera") ?? Find(scene, "MainCamera");
        if (cam != null) cam.name = "MainCamera";

        var sender = avatar.GetComponent<MetaAvatarSender>();
        if (sender != null) Object.DestroyImmediate(sender);
        var input = avatar.GetComponent<SampleInputManager>();
        if (input != null) Object.DestroyImmediate(input);

        Selection.activeGameObject = avatar.gameObject;
        if (!EditorApplication.ExecuteMenuItem("GCT600/Meta Avatar/Configure Selected As Receiver"))
            log.AppendLine("Wall: Configure Selected As Receiver menu failed");
        var receiver = avatar.GetComponent<MetaAvatarReceiver>();
        receiver.avatar = avatar;
        receiver.applyRootPose = false;
        receiver.connectToRelay = true;
        receiver.serverAddress = "127.0.0.1";
        receiver.serverPort = 5060;
        receiver.channel = "quest-user";

        SetStreamManagerIp(scene, "127.0.0.1", log);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        log.AppendLine("Wall: " + Describe(scene));
    }

    private static void SetStreamManagerIp(Scene scene, string ip, StringBuilder log)
    {
        var sm = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<StreamManager>(true)).FirstOrDefault();
        if (sm == null) { log.AppendLine(scene.name + ": no StreamManager"); return; }
        sm.wall1.ipAddress = ip;
        sm.wall1.activeType = StreamClient.ClientType.Pose;
        EditorUtility.SetDirty(sm);
    }

    private static OvrAvatarEntity FindAvatar(Scene scene)
    {
        return scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<OvrAvatarEntity>(true)).First();
    }

    private static GameObject FindRigByComponent(Scene scene)
    {
        var rig = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<OVRCameraRig>(true)).FirstOrDefault();
        return rig != null ? rig.gameObject : null;
    }

    private static GameObject Find(Scene scene, string name)
    {
        return scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
            .Select(t => t.gameObject).FirstOrDefault(g => g.name == name);
    }

    private static string Describe(Scene scene)
    {
        var sb = new StringBuilder();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            sb.Append("\n  ").Append(root.name);
            foreach (Transform child in root.transform) sb.Append("\n    ").Append(child.name);
        }
        return sb.ToString();
    }
}
