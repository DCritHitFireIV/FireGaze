namespace FireGaze.RepoAudit;

// UI-only compile dependency; no repository/network behavior is substituted by these tests.
public enum RepoStatus { Unknown, Ok, Empty, Invalid, Dead, Blocked, Unreachable }
