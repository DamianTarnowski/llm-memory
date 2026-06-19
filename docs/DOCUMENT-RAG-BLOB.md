# Document RAG + Homelab Blob plan

`llm-memory` should not store large files directly. Homelab Blob should own
bytes, quotas, object keys, signed URLs and retention. `llm-memory` should own
the searchable index, chunks, embeddings, summaries, graph links and provenance.

## Target shape

Source of truth:
- Homelab Blob: original object bytes and object lifecycle.
- `llm-memory`: document metadata, extracted text, chunks, embeddings, search
  logs, route traces, feedback and graph relations.

Core rule:
- Every memory/document hit must cite its source: blob object id/key/version,
  project, file name, page/offset and ingest/checksum metadata.

## Query modes

Smart MCP callers can set `search_memory` routing parameters directly. The
optional `QueryRouting` mini-model is only a fallback for simpler clients.

Useful route modes:
- `memory_light`: project/personal memory lookup, low cost.
- `memory_medium`: normal vector + BM25 + optional graph/reranker.
- `heavy_rag`: vague follow-ups, architecture questions, multi-hop memory.
- `graph_rag`: relationships between people, projects, repos, decisions and
  entities.
- `document_rag`: questions that need indexed Blob-backed documents.
- `no_rag`: caller should not search.
- `write_memory`: caller should save/ingest, not search.

`document_rag` is currently a contract, not a full retriever. Keep
`QueryRouting:AllowDocumentRag=false` until the tables and retriever below exist.

## Proposed schema

`memory.blob_objects`
- `id uuid primary key`
- `project_id uuid not null`
- `blob_project_key text not null`
- `blob_object_key text not null`
- `blob_version text null`
- `content_type text`
- `file_name text`
- `byte_size bigint`
- `sha256 text`
- `created_at timestamptz`
- `last_seen_at timestamptz`

`memory.documents`
- `id uuid primary key`
- `project_id uuid not null`
- `blob_object_id uuid references memory.blob_objects(id)`
- `title text`
- `source_uri text`
- `source_type text` (`blob`, `repo`, `web`, `upload`, `generated`)
- `language text`
- `status text` (`pending`, `extracting`, `indexed`, `failed`, `deleted`)
- `summary text`
- `metadata jsonb`
- `created_at timestamptz`
- `updated_at timestamptz`

`memory.document_chunks`
- `id uuid primary key`
- `project_id uuid not null`
- `document_id uuid references memory.documents(id)`
- `chunk_index int`
- `page_number int null`
- `section_path text null`
- `char_start int null`
- `char_end int null`
- `content text not null`
- `content_tsv tsvector`
- `embedding vector(3072)`
- `metadata jsonb`
- `created_at timestamptz`

`memory.document_feedback`
- `id bigserial primary key`
- `project_id uuid not null`
- `user_id uuid null`
- `query_text text not null`
- `document_chunk_id uuid not null`
- `rating int`
- `feedback_type text`
- `comment text`
- `created_at timestamptz`

## Ingest flow

1. DevHub or an agent uploads/stores the file in Homelab Blob.
2. DevHub calls `llm-memory` with blob metadata, not raw bytes.
3. Document worker downloads the object through a short-lived signed URL or
   internal Blob API.
4. Extract text by content type:
   - markdown/text/json: direct text extraction;
   - PDF/docx: parser/OCR pipeline;
   - image: vision caption + optional OCR;
   - code/archive: repo-aware chunking later.
5. Chunk with stable page/offset metadata.
6. Write `documents` and `document_chunks`, compute embeddings, update FTS.
7. Extract entities and relations into Apache AGE.
8. Save route/search logs with returned chunk ids and Blob provenance.

## Retrieval flow

For `document_rag`:
- Rewrite follow-up to standalone query before retrieval.
- Run document vector search and document BM25.
- Optionally run graph expansion from document entities.
- Fuse document chunks with memory notes only when the caller allows mixed
  memory/document scope.
- Rerank against the standalone query.
- Enrich top chunks with parent/neighbor chunks from the same document.
- Return citations with blob object id/key, document id, page/offset and chunk id.

For `memory_medium` / `heavy_rag`:
- Keep current note search as the primary path.
- Optionally include a small document side search only if route/context says the
  active task is document-grounded.

## DevHub integration

Useful first UI:
- Documents tab: object, status, chunks, last indexed time, checksum.
- Search trace tab: original query, standalone query, route, stream weights,
  memory hits, document hits, graph hits, reranker scores.
- Reindex button per document.
- Feedback buttons on memory/document hits.
- Blob link/download only through existing Blob auth/signed URL flow.

## Guardrails

- Never save secrets extracted from documents as durable memory facts.
- Do not let route settings bypass tenant/project ACL.
- Do not duplicate Blob bytes in Postgres.
- Do not enable `document_rag` until document chunks have RLS and project
  isolation tests.
- Keep document retrieval behind feature flags and log route traces before
  making it default.
