"""Policy boundary for player contributions.

Plugin Name/Punchline/Description are maintained by FireGaze maintainers. The
legacy issue archive and history remain readable, but public player payloads
must not be accepted for these metadata fields. UI text contributions use a
separate payload and are intentionally outside this policy.
"""

PLAYER_METADATA_FIELDS = frozenset({"Name", "Punchline", "Description"})


def reject_player_metadata_field(field: str) -> str | None:
    if str(field or "").strip() in PLAYER_METADATA_FIELDS:
        return "插件简介元数据仅由维护者维护，不接受玩家投稿"
    return None
