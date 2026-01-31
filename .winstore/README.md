# .winstore - Windows Store Listing Metadata

Easydict for Windows is now available on the Microsoft Store.

<a href="https://apps.microsoft.com/detail/9p7nqvxf9dzj">
  <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Get it from Microsoft" width="200" />
</a>

**Store URL**: https://apps.microsoft.com/detail/9p7nqvxf9dzj

This directory maintains the Microsoft Store listing metadata for Easydict for Windows.

## Directory Structure

```
.winstore/
├── README.md              # This file
├── store-config.json      # Store-level configuration (app ID, languages, submission settings)
├── listings/              # Per-language listing metadata
│   ├── en-us.json         # English (primary)
│   ├── zh-cn.json         # Simplified Chinese
│   ├── zh-tw.json         # Traditional Chinese
│   ├── ja-jp.json         # Japanese
│   ├── ko-kr.json         # Korean
│   ├── fr-fr.json         # French
│   └── de-de.json         # German
└── scripts/
    └── Sync-StoreListings.ps1  # Sync script for Partner Center
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
- **Primary language**: `en-us` is the primary language. When making content changes, update `en-us.json` first, then translate to other languages.
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

The validation script (`Sync-StoreListings.ps1 -Mode validate`) checks these limits automatically.

### Adding a New Language

1. Add the language code to `store-config.json` > `listing.languages`
2. Create a new `listings/<lang>.json` file by copying `en-us.json` as the template
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
