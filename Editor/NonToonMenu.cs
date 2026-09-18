using System.Linq;
using TsiYuki.Core.Editor;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace TsiYuki.NonToon.Editor
{
    /// <summary>
    /// TsiYuki/NonToon Converter: selects the avatar's converter, adding one to the avatar root if it has none.
    /// The avatar is the one containing the selection, or the only active avatar in the open scenes.
    /// </summary>
    internal static class NonToonMenu
    {
        private const string Path = YukiMenu.Root + "NonToon Converter";
        private static YukiLocalizer L => YukiNonToonConverterEditor.L;

        [MenuItem(Path)]
        private static void Open()
        {
            var avatar = FindTarget();
            if (avatar == null)
            {
                EditorUtility.DisplayDialog("Yuki NonToon", L["menu.no_avatar"], L["ui.ok"]);
                return;
            }

            var converter = avatar.GetComponentInChildren<YukiNonToonConverter>(true);
            if (converter == null)
            {
                converter = Undo.AddComponent<YukiNonToonConverter>(avatar.gameObject);
            }
            Selection.activeObject = converter.gameObject;
            EditorGUIUtility.PingObject(converter.gameObject);
        }

        private static VRCAvatarDescriptor FindTarget()
        {
            var selected = Selection.activeGameObject;
            if (selected != null)
            {
                var fromSelection = selected.GetComponentInParent<VRCAvatarDescriptor>(true);
                if (fromSelection != null) return fromSelection;
            }
            var active = Object.FindObjectsOfType<VRCAvatarDescriptor>()
                .Where(d => d.gameObject.activeInHierarchy && !EditorUtility.IsPersistent(d))
                .ToArray();
            return active.Length == 1 ? active[0] : null;
        }
    }
}
