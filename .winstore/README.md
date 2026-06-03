# .winstore - Windows Store Listing Metadata

This directory maintains the Microsoft Store listing metadata for Easydict for Windows.

## Directory Structure

```
.winstore/
├── README.md              # This file
├── store-config.json      # Store-level configuration (app ID, languages, submission settings)
├── listings/              # Per-language listing metadata
│   ├── en-us.yaml         # English (primary)
│   ├── zh-cn.yaml         # Simplified Chinese
│   ├── zh-tw.yaml         # Traditional Chinese
│   ├── ja-jp.yaml         # Japanese
│   └── ko-kr.yaml         # Korean
└── scripts/
    └── Sync-StoreListings.ps1  # Cargo shim for the Rust listing tool
```

## Important Notes

### Keywords Restrictions

**Do NOT include third-party product names in keywords.** Microsoft Store policy prohibits using competitor or third-party trademarks as keywords. The following names (and similar) must never appear in the `keywords` array:

- DeepL, DeepSeek
- OpenAI, ChatGPT, GPT
- Google, Gemini
- Ollama, Groq, GitHub Models
- Any other third-party service or product name

These names may appear in the `description` and `features` fields when describing the services that Easydict supports, but **never in `keywords`**.

### Content Guidelines

- **Open-source emphasis**: The `description` must highlight "free and open-source" and the GPL-3.0 license in the first sentence.
- **Supported languages**: Store listings are limited to `en-us`, `zh-cn`, `zh-tw`, `ja-jp`, and `ko-kr`. Do not add another Store listing language without explicit approval.
- **Primary language**: `en-us` is the primary language. When making content changes, update `en-us.yaml` first, then translate to other languages.
- **Consistency**: All language files must have the same structure and cover the same features. Do not add features to one language that are missing from others.

### Microsoft Store Limits

| Field             | Max Length | Max Count |
|-------------------|-----------|-----------|
| title             | 256 chars | -         |
| shortDescription  | 100 chars | -         |
| description       | 10000 chars | -       |
| features (each)   | 200 chars | 20 items  |
| keywords (each)   | 40 chars  | 7 items   |
| releaseNotes      | 1500 chars | -        |

The Rust validation tool (`Sync-StoreListings.ps1 -Mode validate`, backed by `easydict_store_listings`) checks these limits automatically.

### Adding a New Language

Store listing languages are currently capped at the five languages above. Do not follow these steps without explicit approval to expand the Microsoft Store listing language set.

1. Add the language code to `store-config.json` > `listing.languages`
2. Create a new `listings/<lang>.yaml` file by copying `en-us.yaml` as the template
3. Translate all fields
4. Run validation: `.\scripts\Sync-StoreListings.ps1 -Mode validate -Languages <lang>`
5. Add the corresponding language resource in `dotnet/src/Easydict.WinUI/Strings/<lang>/` and update `Package.appxmanifest`

### GitHub Actions Workflow

The `store-listings.yml` workflow (Actions > Store Listings Management) supports three actions:

- **validate** - Check all listing files for errors (safe, no side effects)
- **preview** - Display what would be submitted
- **submit** - Push listing updates to Partner Center via `msstore` CLI

Submit requires three repository secrets for Azure AD authentication:

- `MSSTORE_TENANT_ID`
- `MSSTORE_CLIENT_ID`
- `MSSTORE_CLIENT_SECRET`

See [Microsoft Store Developer CLI docs](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/overview) for setup instructions.
