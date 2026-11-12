<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// drive-lib.php: Google Drive API v3 with the owner's refresh token (~/co-agent/secrets/google-session.json,
// kept fresh by ping.php). Used by cron/drive-sync.php (step 2 of the Drive → DB migration, 2026-10-15).
declare(strict_types=1);

require_once __DIR__ . '/db-lib.php';

/** Returns a cached OAuth access token, refreshed 5 minutes before expiry. */
function co_drive_token(): string
{
    static $token = null;
    static $expires = 0;
    if ($token !== null && time() < $expires) {
        return $token;
    }

    $file = co_agent_home() . '/co-agent/secrets/google-session.json';
    $raw = @file_get_contents($file);
    if ($raw === false) {
        throw new RuntimeException('drive token: session file missing');
    }
    $sess = json_decode($raw, true);
    if (!is_array($sess) || empty($sess['rt'])) {
        throw new RuntimeException('drive token: no refresh token');
    }

    $cfg = co_config();
    [$status, $json] = co_post_form('https://oauth2.googleapis.com/token', [
        'grant_type'    => 'refresh_token',
        'refresh_token' => $sess['rt'],
        'client_id'     => $cfg['client_id'],
        'client_secret' => $cfg['client_secret'],
    ]);
    if ($status !== 200 || empty($json['access_token'])) {
        throw new RuntimeException('drive token: ' . ($json['error'] ?? $status));
    }

    $token = (string)$json['access_token'];
    $expires = time() + max(0, (int)($json['expires_in'] ?? 3600) - 300);
    return $token;
}

/** Performs an authenticated GET with retries; returns the raw body of a 2xx response. */
function co_drive_raw(string $path, array $query = [], int $maxBytes = 0): string
{
    $url = 'https://www.googleapis.com/drive/v3/' . $path;
    if ($query) {
        $url .= '?' . http_build_query($query);
    }
    $delays = [1, 2, 4];

    for ($attempt = 0; ; $attempt++) {
        $tooBig = false;
        $ch = curl_init($url);
        $opts = [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_FOLLOWLOCATION => true,
            CURLOPT_CONNECTTIMEOUT => 10,
            CURLOPT_TIMEOUT        => 30,
            CURLOPT_HTTPHEADER     => ['Authorization: Bearer ' . co_drive_token()],
        ];
        if ($maxBytes > 0) {
            // Abort as soon as the declared or received size exceeds the limit
            $opts[CURLOPT_NOPROGRESS] = false;
            $opts[CURLOPT_PROGRESSFUNCTION] =
                static function ($h, $dlTotal, $dlNow, $ulTotal, $ulNow) use ($maxBytes, &$tooBig): int {
                    if ($dlTotal > $maxBytes || $dlNow > $maxBytes) {
                        $tooBig = true;
                        return 1;
                    }
                    return 0;
                };
        }
        curl_setopt_array($ch, $opts);
        $body  = curl_exec($ch);
        $errno = curl_errno($ch);
        $err   = curl_error($ch);
        $code  = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        curl_close($ch);

        if ($tooBig) {
            throw new RuntimeException("drive $path: file exceeds $maxBytes bytes");
        }

        $retryable = $errno !== 0 || $code === 429 || $code >= 500;
        if ($retryable && $attempt < count($delays)) {
            sleep($delays[$attempt]);
            continue;
        }
        if ($errno !== 0) {
            throw new RuntimeException("drive $path: curl error $errno: $err");
        }
        if ($code < 200 || $code >= 300) {
            throw new RuntimeException("drive $path: HTTP $code " . substr((string)$body, 0, 200));
        }
        return (string)$body;
    }
}

/** GET a Drive API endpoint and return the decoded JSON. */
function co_drive_get(string $path, array $query = []): array
{
    $body = co_drive_raw($path, $query);
    $json = json_decode($body, true);
    if (!is_array($json)) {
        throw new RuntimeException("drive $path: invalid JSON");
    }
    return $json;
}

/** Download file content, aborting if larger than $maxBytes. */
function co_drive_download(string $fileId, int $maxBytes = 8_000_000): string
{
    $body = co_drive_raw('files/' . rawurlencode($fileId), ['alt' => 'media'], $maxBytes);
    if (strlen($body) > $maxBytes) {
        throw new RuntimeException("drive download $fileId: file exceeds $maxBytes bytes");
    }
    return $body;
}

/** Current start page token for the changes feed. */
function co_drive_start_token(): string
{
    $json = co_drive_get('changes/startPageToken');
    if (empty($json['startPageToken'])) {
        throw new RuntimeException('drive changes/startPageToken: missing token');
    }
    return (string)$json['startPageToken'];
}

/** One page of the changes feed. */
function co_drive_changes(string $pageToken): array
{
    return co_drive_get('changes', [
        'pageToken'      => $pageToken,
        'pageSize'       => 1000,
        'spaces'         => 'drive',
        'includeRemoved' => 'true',
        'fields'         => 'nextPageToken,newStartPageToken,changes(fileId,removed,file(id,name,mimeType,parents,trashed))',
    ]);
}

/** File metadata with in-process cache; null if not found. */
function co_drive_meta(string $fileId): ?array
{
    static $cache = [];
    if (array_key_exists($fileId, $cache)) {
        return $cache[$fileId];
    }
    try {
        $meta = co_drive_get('files/' . rawurlencode($fileId), [
            'fields' => 'id,name,mimeType,parents,trashed',
        ]);
    } catch (RuntimeException $e) {
        if (!str_contains($e->getMessage(), 'HTTP 404')) {
            throw $e;
        }
        $meta = null;
    }
    return $cache[$fileId] = $meta;
}

/** Names of parent, grandparent, ... up to $depth levels (first parent only). */
function co_drive_ancestor_names(string $fileId, int $depth): array
{
    $names = [];
    $id = $fileId;
    for ($i = 0; $i < $depth; $i++) {
        $meta = co_drive_meta($id);
        $parentId = $meta['parents'][0] ?? null;
        if ($parentId === null) {
            break;
        }
        $parent = co_drive_meta((string)$parentId);
        if ($parent === null) {
            break;
        }
        $names[] = (string)($parent['name'] ?? '');
        $id = (string)$parentId;
    }
    return $names;
}

// ---- Uploads (schema 9: memory.md written by db.php memory_decide) ----

/** Drive request with a body; PATCH is retried once after 2 s on curl error, 429 or 5xx (POST is not: no duplicates). */
function co_drive_send(string $method, string $url, string $body, string $contentType): string
{
    $maxAttempts = $method === 'PATCH' ? 2 : 1;
    for ($attempt = 1; ; $attempt++) {
        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_CUSTOMREQUEST  => $method,
            CURLOPT_POSTFIELDS     => $body,
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_CONNECTTIMEOUT => 10,
            CURLOPT_TIMEOUT        => 60,
            CURLOPT_HTTPHEADER     => [
                'Authorization: Bearer ' . co_drive_token(),
                'Content-Type: ' . $contentType,
                'Expect:',
            ],
        ]);
        $response = curl_exec($ch);
        $errno    = curl_errno($ch);
        $error    = curl_error($ch);
        $status   = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        curl_close($ch);

        $respBody  = is_string($response) ? $response : '';
        $retryable = $errno !== 0 || $status === 429 || $status >= 500;
        if ($retryable && $attempt < $maxAttempts) {
            sleep(2);
            continue;
        }
        if ($errno !== 0) {
            throw new RuntimeException('drive upload: curl error ' . $errno . ': ' . substr($error, 0, 200));
        }
        if ($status < 200 || $status >= 300) {
            throw new RuntimeException(sprintf('drive upload: HTTP %d %s', $status, substr($respBody, 0, 200)));
        }
        return $respBody;
    }
}

function co_drive_extract_id(string $response): string
{
    $data = json_decode($response, true);
    if (!is_array($data) || !is_string($data['id'] ?? null) || $data['id'] === '') {
        throw new RuntimeException('drive upload: missing id in response ' . substr($response, 0, 200));
    }
    return $data['id'];
}

/** Replaces the content of an existing file. */
function co_drive_update_text(string $fileId, string $text, string $mime = 'text/markdown'): void
{
    co_drive_send('PATCH', 'https://www.googleapis.com/upload/drive/v3/files/' . rawurlencode($fileId)
        . '?uploadType=media&supportsAllDrives=true', $text, $mime);
}

/** Creates a file with content (multipart upload); returns its id. */
function co_drive_create_text(string $parentId, string $name, string $text, string $mime = 'text/markdown'): string
{
    $metadata = json_encode(['name' => $name, 'parents' => [$parentId], 'mimeType' => $mime],
        JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_THROW_ON_ERROR);
    $boundary = 'co_' . bin2hex(random_bytes(16));
    $body = "--{$boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n" . $metadata . "\r\n"
        . "--{$boundary}\r\nContent-Type: {$mime}\r\n\r\n" . $text . "\r\n--{$boundary}--\r\n";
    return co_drive_extract_id(co_drive_send('POST',
        'https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&supportsAllDrives=true&fields=id',
        $body, 'multipart/related; boundary=' . $boundary));
}

/** Creates a folder; returns its id. */
function co_drive_create_folder(string $parentId, string $name): string
{
    $body = json_encode(['name' => $name, 'mimeType' => 'application/vnd.google-apps.folder', 'parents' => [$parentId]],
        JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_THROW_ON_ERROR);
    return co_drive_extract_id(co_drive_send('POST', 'https://www.googleapis.com/drive/v3/files?supportsAllDrives=true&fields=id',
        $body, 'application/json; charset=UTF-8'));
}
