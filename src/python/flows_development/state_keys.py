"""Chaves do `state_store.data` usadas por `tasks.py` e `prompts.py` — módulo próprio (em
vez de definidas num dos dois) para não criar import circular entre eles (`tasks.py` já
importa `prompts`)."""

from __future__ import annotations

CURRENT_FEATURE_ID = "current_feature_id"
CURRENT_FEATURE_TITLE = "current_feature_title"
CURRENT_FEATURE_SUMMARY = "current_feature_summary"
CURRENT_FEATURE_VERIFY = "current_feature_verify"
FEATURE_STEPS = "feature_steps"

# Não é uma chave do state_store — é o nome do artefato do brief no artifact_store
# (.harness/brief.md). Vive aqui pelo mesmo motivo das chaves acima: tasks.py e prompts.py
# precisam do mesmo valor, sem criar import circular entre eles.
BRIEF_ARTIFACT_NAME = "brief"
