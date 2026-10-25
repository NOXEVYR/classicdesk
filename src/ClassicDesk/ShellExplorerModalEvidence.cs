namespace ClassicDesk;

/// <summary>Classifies observed blockers only; never reads or changes real windows.</summary>
public static class ShellExplorerModalEvidence
{
    public static bool? Evaluate(bool? ownerEnabled, bool complete, IEnumerable<bool?> ownedVisibility)
    {
        // A disabled owner is sufficient evidence of a blocker. Missing evidence
        // must never be converted into permission to replace a window.
        if (ownerEnabled == false) return true;
        bool unknown = ownerEnabled is null || !complete;
        foreach (bool? visible in ownedVisibility)
        {
            if (visible == true) return true;
            if (visible is null) unknown = true;
        }
        return unknown ? null : false;
    }
}
