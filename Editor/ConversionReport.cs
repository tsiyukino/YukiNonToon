using System.Collections.Generic;
using System.Linq;
using TsiYuki.Core.Editor;
using UnityEngine;

namespace TsiYuki.NonToon.Editor
{
    /// <summary>What happened to one lilToon feature of one material.</summary>
    internal readonly struct ReportLine
    {
        public readonly YukiStatus Status;
        public readonly string Key;       // localization key: feature.xxx
        public readonly object[] Args;

        public ReportLine(YukiStatus status, string key, object[] args)
        {
            Status = status;
            Key = key;
            Args = args;
        }

        public string Text(YukiLocalizer l) => l.Tr(Key, Args);
    }

    internal sealed class MaterialReport
    {
        public Material Source;
        public readonly List<ReportLine> Lines = new List<ReportLine>();

        public void Ok(string key, params object[] args) => Lines.Add(new ReportLine(YukiStatus.Ok, key, args));
        public void Approx(string key, params object[] args) => Lines.Add(new ReportLine(YukiStatus.Approximate, key, args));
        public void Lost(string key, params object[] args) => Lines.Add(new ReportLine(YukiStatus.Problem, key, args));

        public YukiStatus Worst
        {
            get
            {
                if (Lines.Any(l => l.Status == YukiStatus.Problem)) return YukiStatus.Problem;
                if (Lines.Any(l => l.Status == YukiStatus.Approximate)) return YukiStatus.Approximate;
                return YukiStatus.Ok;
            }
        }
    }
}
