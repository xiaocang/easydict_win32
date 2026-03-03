using System.Net;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

/// <summary>
/// Tests for YoudaoService (web dictionary + web translate + official API).
/// </summary>
public class YoudaoServiceTests
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly YoudaoService _service;

    public YoudaoServiceTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _service = new YoudaoService(_httpClient);
    }

    [Fact]
    public void ServiceId_IsYoudao()
    {
        _service.ServiceId.Should().Be("youdao");
    }

    [Fact]
    public void DisplayName_IsYoudao()
    {
        _service.DisplayName.Should().Be("📖 Youdao");
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        _service.RequiresApiKey.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_AlwaysTrue()
    {
        _service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void SupportedLanguages_ContainsMajorLanguages()
    {
        var languages = _service.SupportedLanguages;
        languages.Should().Contain(Language.SimplifiedChinese);
        languages.Should().Contain(Language.English);
        languages.Should().Contain(Language.Japanese);
        languages.Should().Contain(Language.Korean);
    }

    [Fact]
    public async Task TranslateAsync_WebDict_ParsesUSUKPhonetics()
    {
        // Arrange - Youdao web dict response with US/UK phonetics
        var response = """
            {
                "simple": {
                    "word": {
                        "usphone": "həˈloʊ",
                        "usspeech": "hello&type=1",
                        "ukphone": "həˈləʊ",
                        "ukspeech": "hello&type=2"
                    }
                },
                "ec": {
                    "word": {
                        "trs": [
                            {
                                "pos": "int.",
                                "tran": "喂；你好"
                            },
                            {
                                "pos": "n.",
                                "tran": "表示问候；打招呼"
                            }
                        ]
                    }
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Phonetics.Should().NotBeNull();
        result.WordResult.Phonetics.Should().HaveCount(2);

        var usPhonetic = result.WordResult.Phonetics![0];
        usPhonetic.Text.Should().Be("həˈloʊ");
        usPhonetic.Accent.Should().Be("US");
        usPhonetic.AudioUrl.Should().Contain("dict.youdao.com/dictvoice");
        usPhonetic.AudioUrl.Should().Contain("hello");

        var ukPhonetic = result.WordResult.Phonetics[1];
        ukPhonetic.Text.Should().Be("həˈləʊ");
        ukPhonetic.Accent.Should().Be("UK");
        ukPhonetic.AudioUrl.Should().Contain("dict.youdao.com/dictvoice");
    }

    [Fact]
    public async Task TranslateAsync_WebDict_ParsesDefinitions()
    {
        var response = """
            {
                "simple": {
                    "word": {
                        "usphone": "həˈloʊ"
                    }
                },
                "ec": {
                    "word": {
                        "trs": [
                            {
                                "pos": "int.",
                                "tran": "喂；你好"
                            },
                            {
                                "pos": "n.",
                                "tran": "表示问候；打招呼"
                            },
                            {
                                "pos": "v.",
                                "tran": "打招呼"
                            }
                        ]
                    }
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.WordResult.Should().NotBeNull();
        result.WordResult!.Definitions.Should().NotBeNull();
        result.WordResult.Definitions.Should().HaveCount(3);
        result.WordResult.Definitions![0].PartOfSpeech.Should().Be("int.");
        result.WordResult.Definitions[0].Meanings.Should().Contain("喂；你好");
        result.WordResult.Definitions[1].PartOfSpeech.Should().Be("n.");
        result.WordResult.Definitions[2].PartOfSpeech.Should().Be("v.");
    }

    [Fact]
    public async Task TranslateAsync_WebDict_BuildsTranslatedTextFromDefinitions()
    {
        var response = """
            {
                "simple": {
                    "word": {}
                },
                "ec": {
                    "word": {
                        "trs": [
                            {
                                "pos": "n.",
                                "tran": "苹果"
                            }
                        ]
                    }
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "apple",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.TranslatedText.Should().Contain("苹果");
        result.TranslatedText.Should().Contain("n.");
    }

    [Fact]
    public async Task TranslateAsync_WebDict_ParsesArrayFormatSimpleWord()
    {
        // Arrange - Youdao API v4 sometimes returns simple.word as an array
        var response = """
            {
                "simple": {
                    "word": [
                        {
                            "usphone": "həˈloʊ",
                            "usspeech": "hello&type=2",
                            "ukphone": "həˈləʊ",
                            "ukspeech": "hello&type=1"
                        }
                    ]
                },
                "ec": {
                    "word": {
                        "trs": [
                            {
                                "pos": "int.",
                                "tran": "喂；你好"
                            }
                        ]
                    }
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert - Should correctly parse phonetics from array format
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Phonetics.Should().NotBeNull();
        result.WordResult.Phonetics.Should().HaveCount(2);

        var usPhonetic = result.WordResult.Phonetics!.First(p => p.Accent == "US");
        usPhonetic.Text.Should().Be("həˈloʊ");

        var ukPhonetic = result.WordResult.Phonetics!.First(p => p.Accent == "UK");
        ukPhonetic.Text.Should().Be("həˈləʊ");
    }

    [Fact]
    public async Task TranslateAsync_WebDict_ParsesArrayFormatEcWord()
    {
        // Arrange - Youdao API v4 sometimes returns ec.word as an array
        var response = """
            {
                "simple": {
                    "word": {}
                },
                "ec": {
                    "word": [
                        {
                            "usphone": "ˈæpəl",
                            "usspeech": "apple&type=2",
                            "trs": [
                                {
                                    "pos": "n.",
                                    "tran": "苹果；苹果公司"
                                }
                            ]
                        }
                    ]
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "apple",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert - Should correctly parse phonetics and definitions from ec.word array format
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Phonetics.Should().NotBeNull();
        result.WordResult.Phonetics.Should().HaveCount(1);
        result.WordResult.Phonetics![0].Text.Should().Be("ˈæpəl");
        result.WordResult.Phonetics![0].Accent.Should().Be("US");

        result.WordResult.Definitions.Should().NotBeNull();
        result.WordResult.Definitions.Should().HaveCount(1);
        result.WordResult.Definitions![0].PartOfSpeech.Should().Be("n.");
        result.WordResult.Definitions![0].Meanings.Should().Contain("苹果；苹果公司");
    }

    // Mock response for the dynamic sign key endpoint
    private static readonly string _mockKeyResponse = """
        {
            "code": 0,
            "msg": "OK",
            "data": {
                "secretKey": "mockSecretKeyForTesting123"
            }
        }
        """;

    [Fact]
    public async Task TranslateAsync_WebTranslate_ParsesTranslatedText()
    {
        // Arrange - Long sentence goes to web translate API
        // First response is for the key endpoint, second is for translation
        _mockHandler.EnqueueJsonResponse(_mockKeyResponse);
        var response = """
            {
                "translateResult": [
                    [
                        {
                            "src": "This is a test sentence.",
                            "tgt": "这是一个测试句子。"
                        }
                    ]
                ]
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "This is a test sentence.",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("这是一个测试句子。");
        result.OriginalText.Should().Be("This is a test sentence.");
        result.ServiceName.Should().Be("📖 Youdao");
        result.WordResult.Should().BeNull();
    }

    [Fact]
    public async Task TranslateAsync_WebTranslate_HandlesMultipleParagraphs()
    {
        // First response is for the key endpoint, second is for translation
        _mockHandler.EnqueueJsonResponse(_mockKeyResponse);
        var response = """
            {
                "translateResult": [
                    [
                        {
                            "src": "First paragraph.",
                            "tgt": "第一段。"
                        }
                    ],
                    [
                        {
                            "src": "Second paragraph.",
                            "tgt": "第二段。"
                        }
                    ]
                ]
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "First paragraph.\nSecond paragraph.",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        result.TranslatedText.Should().Contain("第一段");
        result.TranslatedText.Should().Contain("第二段");
    }

    [Fact]
    public async Task TranslateAsync_OpenApi_ParsesTranslationAndPhonetics()
    {
        // Arrange - Configure with API keys
        _service.Configure("testAppKey", "testAppSecret", useOfficialApi: true);

        var response = """
            {
                "errorCode": "0",
                "translation": ["你好"],
                "basic": {
                    "us-phonetic": "həˈloʊ",
                    "us-speech": "https://dict.youdao.com/dictvoice?audio=hello&type=1",
                    "uk-phonetic": "həˈləʊ",
                    "uk-speech": "https://dict.youdao.com/dictvoice?audio=hello&type=2",
                    "explains": [
                        "int. 喂；你好",
                        "n. 表示问候"
                    ]
                },
                "l": "en2zh-CHS"
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.Auto,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.TranslatedText.Should().Be("你好");
        result.DetectedLanguage.Should().Be(Language.English);
        
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Phonetics.Should().HaveCount(2);
        result.WordResult.Phonetics![0].Accent.Should().Be("US");
        result.WordResult.Phonetics![0].AudioUrl.Should().Contain("type=1");
        result.WordResult.Phonetics![1].Accent.Should().Be("UK");
        result.WordResult.Phonetics![1].AudioUrl.Should().Contain("type=2");
        
        result.WordResult.Definitions.Should().HaveCount(2);
        result.WordResult.Definitions![0].PartOfSpeech.Should().Be("int");
        result.WordResult.Definitions![0].Meanings.Should().Contain("喂；你好");
        result.WordResult.Definitions![1].PartOfSpeech.Should().Be("n");
    }

    [Fact]
    public async Task TranslateAsync_OpenApi_HandlesDefinitionWithoutPartOfSpeech()
    {
        _service.Configure("testAppKey", "testAppSecret", useOfficialApi: true);

        var response = """
            {
                "errorCode": "0",
                "translation": ["测试"],
                "basic": {
                    "explains": [
                        "a test",
                        "testing"
                    ]
                },
                "l": "zh-CHS2en"
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "测试",
            FromLanguage = Language.SimplifiedChinese,
            ToLanguage = Language.English
        };

        var result = await _service.TranslateAsync(request);

        result.WordResult.Should().NotBeNull();
        result.WordResult!.Definitions.Should().HaveCount(2);
        result.WordResult.Definitions![0].PartOfSpeech.Should().BeNull();
        result.WordResult.Definitions![0].Meanings.Should().Contain("a test");
    }

    [Fact]
    public async Task TranslateAsync_OpenApi_ThrowsOnInvalidApiKey()
    {
        _service.Configure("invalidKey", "invalidSecret", useOfficialApi: true);

        var response = """
            {
                "errorCode": "401",
                "translation": []
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var act = async () => await _service.TranslateAsync(request);

        await act.Should().ThrowAsync<TranslationException>()
            .Where(e => e.ErrorCode == TranslationErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task TranslateAsync_OpenApi_ThrowsOnRateLimit()
    {
        _service.Configure("testKey", "testSecret", useOfficialApi: true);

        var response = """
            {
                "errorCode": "411",
                "translation": []
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var act = async () => await _service.TranslateAsync(request);

        await act.Should().ThrowAsync<TranslationException>()
            .Where(e => e.ErrorCode == TranslationErrorCode.RateLimited);
    }

    [Fact]
    public async Task TranslateAsync_Http429_ThrowsRateLimitError()
    {
        _mockHandler.EnqueueErrorResponse(HttpStatusCode.TooManyRequests);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var act = async () => await _service.TranslateAsync(request);

        await act.Should().ThrowAsync<TranslationException>()
            .Where(e => e.ErrorCode == TranslationErrorCode.RateLimited);
    }

    [Fact]
    public async Task TranslateAsync_PhoneticTargetFiltering_USUKAsTargetLanguage()
    {
        // Test that US/UK phonetics work with PhoneticDisplayHelper.GetTargetPhonetics
        var response = """
            {
                "simple": {
                    "word": {
                        "usphone": "test",
                        "ukphone": "test"
                    }
                },
                "ec": {
                    "word": {
                        "trs": [{"pos": "n.", "tran": "测试"}]
                    }
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "test",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        var result = await _service.TranslateAsync(request);

        // Verify that phonetics can be extracted with target filter
        var targetPhonetics = PhoneticDisplayHelper.GetTargetPhonetics(result);
        targetPhonetics.Should().HaveCount(2);
        targetPhonetics.Should().Contain(p => p.Accent == "US");
        targetPhonetics.Should().Contain(p => p.Accent == "UK");
    }

    [Fact]
    public async Task TranslateAsync_WebDict_ParsesWordForms()
    {
        // Arrange - Youdao web dict response with word forms (wfs)
        var response = """
            {
                "simple": {
                    "word": {
                        "usphone": "rʌn"
                    }
                },
                "ec": {
                    "word": {
                        "trs": [
                            {
                                "pos": "v.",
                                "tran": "跑；奔跑"
                            }
                        ],
                        "wfs": [
                            { "wf": { "name": "过去式", "value": "ran" } },
                            { "wf": { "name": "过去分词", "value": "run" } },
                            { "wf": { "name": "现在分词", "value": "running" } }
                        ]
                    }
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "run",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.WordResult.Should().NotBeNull();
        result.WordResult!.WordForms.Should().NotBeNull();
        result.WordResult.WordForms.Should().HaveCount(3);

        result.WordResult.WordForms![0].Name.Should().Be("过去式");
        result.WordResult.WordForms[0].Value.Should().Be("ran");
        result.WordResult.WordForms[1].Name.Should().Be("过去分词");
        result.WordResult.WordForms[1].Value.Should().Be("run");
        result.WordResult.WordForms[2].Name.Should().Be("现在分词");
        result.WordResult.WordForms[2].Value.Should().Be("running");
    }

    [Fact]
    public async Task TranslateAsync_WebDict_ParsesSynonyms()
    {
        // Arrange - Youdao web dict response with synonyms (syno)
        var response = """
            {
                "simple": {
                    "word": {
                        "usphone": "həˈloʊ"
                    }
                },
                "ec": {
                    "word": {
                        "trs": [
                            {
                                "pos": "int.",
                                "tran": "喂；你好"
                            }
                        ]
                    }
                },
                "syno": {
                    "synos": [
                        {
                            "pos": "n.",
                            "tran": "问候",
                            "ws": ["greeting", "salutation"]
                        },
                        {
                            "pos": "v.",
                            "tran": "打招呼",
                            "ws": ["greet", "welcome"]
                        }
                    ]
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Synonyms.Should().NotBeNull();
        result.WordResult.Synonyms.Should().HaveCount(2);

        result.WordResult.Synonyms![0].PartOfSpeech.Should().Be("n.");
        result.WordResult.Synonyms[0].Meaning.Should().Be("问候");
        result.WordResult.Synonyms[0].Words.Should().BeEquivalentTo(["greeting", "salutation"]);

        result.WordResult.Synonyms[1].PartOfSpeech.Should().Be("v.");
        result.WordResult.Synonyms[1].Meaning.Should().Be("打招呼");
        result.WordResult.Synonyms[1].Words.Should().BeEquivalentTo(["greet", "welcome"]);
    }

    [Fact]
    public async Task TranslateAsync_WebDict_WordFormsAndSynonymsCanBeNull()
    {
        // Arrange - Youdao web dict response without wfs or syno
        var response = """
            {
                "simple": {
                    "word": {
                        "usphone": "həˈloʊ"
                    }
                },
                "ec": {
                    "word": {
                        "trs": [
                            {
                                "pos": "int.",
                                "tran": "喂；你好"
                            }
                        ]
                    }
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert
        result.WordResult.Should().NotBeNull();
        result.WordResult!.WordForms.Should().BeNull();
        result.WordResult.Synonyms.Should().BeNull();
    }

    [Fact]
    public async Task TranslateAsync_WebDict_ParsesSynonyms_ObjectFormat()
    {
        // Arrange - Legacy object format: ws is [{w: "greeting"}, ...]
        var response = """
            {
                "simple": {
                    "word": {
                        "usphone": "həˈloʊ"
                    }
                },
                "ec": {
                    "word": {
                        "trs": [
                            {
                                "pos": "int.",
                                "tran": "喂；你好"
                            }
                        ]
                    }
                },
                "syno": {
                    "synos": [
                        {
                            "pos": "n.",
                            "tran": "问候",
                            "ws": [
                                { "w": "greeting" },
                                { "w": "salutation" }
                            ]
                        }
                    ]
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert - Object format should also parse correctly
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Synonyms.Should().NotBeNull();
        result.WordResult.Synonyms.Should().HaveCount(1);
        result.WordResult.Synonyms![0].Words.Should().BeEquivalentTo(["greeting", "salutation"]);
    }

    [Fact]
    public async Task TranslateAsync_WebDict_WordForms_SplitsOrSeparator()
    {
        // Arrange - Word form value contains "或" (Chinese "or") separator
        var response = """
            {
                "simple": {
                    "word": {
                        "usphone": "ˈhæpi"
                    }
                },
                "ec": {
                    "word": {
                        "trs": [
                            {
                                "pos": "adj.",
                                "tran": "快乐的"
                            }
                        ],
                        "wfs": [
                            { "wf": { "name": "比较级", "value": "happier或able to be happy" } },
                            { "wf": { "name": "最高级", "value": "happiest" } }
                        ]
                    }
                }
            }
            """;
        _mockHandler.EnqueueJsonResponse(response);

        var request = new TranslationRequest
        {
            Text = "happy",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        };

        // Act
        var result = await _service.TranslateAsync(request);

        // Assert - "happier或able to be happy" should be split into 2 WordForms
        result.WordResult.Should().NotBeNull();
        result.WordResult!.WordForms.Should().NotBeNull();
        result.WordResult.WordForms.Should().HaveCount(3); // 2 from split + 1 normal

        result.WordResult.WordForms![0].Name.Should().Be("比较级");
        result.WordResult.WordForms[0].Value.Should().Be("happier");
        result.WordResult.WordForms[1].Name.Should().Be("比较级");
        result.WordResult.WordForms[1].Value.Should().Be("able to be happy");
        result.WordResult.WordForms[2].Name.Should().Be("最高级");
        result.WordResult.WordForms[2].Value.Should().Be("happiest");
    }

    [Theory]
    [InlineData("hello", true)]
    [InlineData("hello world", true)]  // Short phrase is allowed
    [InlineData("test-driven", true)]  // Hyphenated words
    [InlineData("don't", true)]        // Apostrophes
    [InlineData("This is a test sentence.", false)]  // Contains period
    [InlineData("Hello!", false)]      // Contains exclamation
    [InlineData("What?", false)]       // Contains question mark
    [InlineData("Line one\nLine two", false)]  // Contains newline
    [InlineData("", false)]            // Empty string
    [InlineData("   ", false)]         // Whitespace only
    public void IsWordQuery_ReturnsExpectedResult(string text, bool expected)
    {
        YoudaoService.IsWordQuery(text).Should().Be(expected);
    }
}
