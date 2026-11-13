<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// transcribe-lib.php of the module dictation (Freedom F10; until CO Web 0.102 the core's web/agent/transcribe.php,
// CO Web 0.30.0): speech-to-text for voice input. CO Web records the audio for the module's composer page (ext/1
// media.audio), the page posts it to the module's upload 'transcribe' (agent/module.php?m=dictation&u=transcribe,
// multipart 'file' + lang, prompt), this forwards it to a Whisper-compatible API (OpenAI or Groq) configured on the
// server. Key: ~/co-agent/secrets/transcribe.json {"provider","key","model"?} (unchanged place). Audio is never stored
// or logged.
declare(strict_types=1);

const CO_TRANSCRIBE_MAX_BYTES = 25 * 1024 * 1024;
const CO_TRANSCRIBE_PROVIDERS = [
    'openai' => [
        'url'   => 'https://api.openai.com/v1/audio/transcriptions',
        'model' => 'gpt-4o-mini-transcribe',
    ],
    'groq' => [
        'url'   => 'https://api.groq.com/openai/v1/audio/transcriptions',
        'model' => 'whisper-large-v3-turbo',
    ],
];

/** @return array{provider:string,key:string,model:string,url:string}|null */
function co_transcribe_config(): ?array
{
    $path = co_agent_home() . '/co-agent/secrets/transcribe.json';
    if (!is_file($path) || !is_readable($path)) return null;
    $raw = @file_get_contents($path);
    if ($raw === false) return null;
    $cfg = json_decode($raw, true);
    if (!is_array($cfg)) return null;

    $provider = is_string($cfg['provider'] ?? null) ? strtolower(trim($cfg['provider'])) : '';
    $key      = is_string($cfg['key'] ?? null) ? trim($cfg['key']) : '';
    if (!isset(CO_TRANSCRIBE_PROVIDERS[$provider]) || $key === '') return null;

    $model = is_string($cfg['model'] ?? null) && trim($cfg['model']) !== ''
        ? trim($cfg['model'])
        : CO_TRANSCRIBE_PROVIDERS[$provider]['model'];

    return [
        'provider' => $provider,
        'key'      => $key,
        'model'    => $model,
        'url'      => CO_TRANSCRIBE_PROVIDERS[$provider]['url'],
    ];
}

/** User op dictation_status: is a speech-to-text service configured (the page then records instead of Web Speech). */
function co_transcribe_status(): array
{
    $config = co_transcribe_config();
    return ['configured' => $config !== null, 'provider' => $config['provider'] ?? null];
}

function co_transcribe_ini_bytes(string $v): int
{
    $v = trim($v);
    if ($v === '') return 0;
    $n = (int)$v;
    switch (strtolower(substr($v, -1))) {
        case 'g': $n *= 1024;
        case 'm': $n *= 1024;
        case 'k': $n *= 1024;
    }
    return $n;
}

function co_transcribe_cut(string $s, int $len): string
{
    return function_exists('mb_substr') ? mb_substr($s, 0, $len, 'UTF-8') : substr($s, 0, $len);
}

function co_transcribe_redact(string $s, string $key): string
{
    if ($key !== '') $s = str_replace($key, '***', $s);
    return (string)preg_replace('/\b(?:sk|gsk)[-_][A-Za-z0-9_\-*]{4,}/', '***', $s);
}

function co_transcribe_ext_for_mime(string $mime): string
{
    $m = strtolower($mime);
    if (str_contains($m, 'mp4') || str_contains($m, 'm4a') || str_contains($m, 'aac')) return 'm4a';
    if (str_contains($m, 'ogg') || str_contains($m, 'opus')) return 'ogg';
    if (str_contains($m, 'mpeg') || str_contains($m, 'mp3')) return 'mp3';
    if (str_contains($m, 'wav')) return 'wav';
    return 'webm';
}

/** Upload 'transcribe': multipart file (+ lang, prompt) of a signed-in member -> {text}; errors answer with co_json. */
function co_transcribe_upload(): array
{
    $config = co_transcribe_config();
    if ($config === null) co_json(503, ['error' => 'not_configured']);

    $contentLength = (int)($_SERVER['CONTENT_LENGTH'] ?? 0);
    $file = $_FILES['file'] ?? null;

    if (!is_array($file) || is_array($file['error'] ?? null)) {
        $postMax = co_transcribe_ini_bytes((string)ini_get('post_max_size'));
        if ($contentLength > CO_TRANSCRIBE_MAX_BYTES + 1024 * 1024 || ($postMax > 0 && $contentLength > $postMax)) {
            co_json(413, ['error' => 'too_large']);
        }
        co_json(400, ['error' => 'missing_audio']);
    }

    $err = (int)($file['error'] ?? UPLOAD_ERR_NO_FILE);
    if ($err === UPLOAD_ERR_INI_SIZE || $err === UPLOAD_ERR_FORM_SIZE) co_json(413, ['error' => 'too_large']);
    if ($err !== UPLOAD_ERR_OK) co_json(400, ['error' => 'upload_failed', 'code' => $err]);

    $tmp  = (string)($file['tmp_name'] ?? '');
    $size = (int)($file['size'] ?? 0);
    if ($tmp === '' || !is_uploaded_file($tmp)) co_json(400, ['error' => 'upload_failed']);
    $realSize = @filesize($tmp);
    if ($realSize !== false) $size = max($size, (int)$realSize);
    if ($size <= 0) co_json(400, ['error' => 'empty_audio']);
    if ($size > CO_TRANSCRIBE_MAX_BYTES) co_json(413, ['error' => 'too_large']);

    $mime = is_string($file['type'] ?? null) && trim($file['type']) !== '' ? trim($file['type']) : 'audio/webm';
    $mime = (string)preg_replace('/[^\w\/\-+.;=, ]/', '', $mime);
    if ($mime === '') $mime = 'audio/webm';

    $name = is_string($file['name'] ?? null) ? basename(str_replace('\\', '/', $file['name'])) : '';
    $name = (string)preg_replace('/[^A-Za-z0-9._-]/', '_', $name);
    if ($name === '' || trim($name, '._') === '') $name = 'audio.webm';
    if (!preg_match('/\.[A-Za-z0-9]{2,5}$/', $name)) $name .= '.' . co_transcribe_ext_for_mime($mime);

    $fields = [
        'file'            => new CURLFile($tmp, $mime, $name),
        'model'           => $config['model'],
        'response_format' => 'json',
    ];

    $lang = $_POST['lang'] ?? null;
    if (is_string($lang) && $lang !== '') {
        $iso = strtolower(trim((string)preg_split('/[-_]/', trim($lang))[0]));
        if (preg_match('/^[a-z]{2}$/', $iso)) $fields['language'] = $iso;
    }

    $prompt = $_POST['prompt'] ?? null;
    if (is_string($prompt)) {
        $prompt = trim($prompt);
        if ($prompt !== '') $fields['prompt'] = co_transcribe_cut($prompt, 500);
    }

    set_time_limit(150);

    $ch = curl_init($config['url']);
    curl_setopt_array($ch, [
        CURLOPT_POST           => true,
        CURLOPT_POSTFIELDS     => $fields,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_HTTPHEADER     => [
            'Authorization: Bearer ' . $config['key'],
            'Accept: application/json',
        ],
        CURLOPT_CONNECTTIMEOUT => 15,
        CURLOPT_TIMEOUT        => 120,
        CURLOPT_SSL_VERIFYPEER => true,
        CURLOPT_SSL_VERIFYHOST => 2,
    ]);

    $body   = curl_exec($ch);
    $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    $cerr   = curl_error($ch);
    curl_close($ch);

    if ($body === false) {
        co_json(502, [
            'error'  => 'provider_failed',
            'detail' => co_transcribe_cut(co_transcribe_redact($cerr !== '' ? $cerr : 'request failed', $config['key']), 300),
        ]);
    }

    $body = (string)$body;
    $data = json_decode($body, true);

    if ($status < 200 || $status >= 300) {
        $detail = '';
        if (is_array($data)) {
            $e = $data['error'] ?? null;
            if (is_array($e) && is_string($e['message'] ?? null)) $detail = $e['message'];
            elseif (is_string($e)) $detail = $e;
            elseif (is_string($data['message'] ?? null)) $detail = $data['message'];
        }
        if ($detail === '') $detail = $body !== '' ? $body : ('HTTP ' . $status);
        co_json(502, [
            'error'  => 'provider_failed',
            'detail' => co_transcribe_cut(co_transcribe_redact(trim($detail), $config['key']), 300),
        ]);
    }

    if (!is_array($data) || !is_string($data['text'] ?? null)) {
        co_json(502, [
            'error'  => 'provider_failed',
            'detail' => co_transcribe_cut(co_transcribe_redact($body !== '' ? $body : 'invalid response', $config['key']), 300),
        ]);
    }

    return ['text' => trim($data['text'])];
}
