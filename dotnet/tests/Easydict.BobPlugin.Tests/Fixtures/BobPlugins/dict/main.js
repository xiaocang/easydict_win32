function translate(query, completion) {
    completion({
        result: {
            from: 'en',
            to: 'zh-Hans',
            toParagraphs: ['单词'],
            toDict: {
                word: query.text,
                phonetics: [
                    { type: 'us', value: '/wərd/', tts: { type: 'url', value: 'https://example.invalid/us.mp3' } },
                    { type: 'uk', value: '/wɜːd/', tts: { type: 'base64', value: 'AAAA' } }
                ],
                parts: [
                    { part: 'n.', means: ['词', '单词'] },
                    { part: 'v.', means: ['措辞'] }
                ],
                exchanges: [
                    { name: '复数', words: ['words'] },
                    { name: '过去式', words: ['worded'] }
                ],
                relatedWordParts: [
                    { part: 'n.', words: [{ word: 'term', means: ['术语'] }, { word: 'vocable' }] }
                ],
                additions: [
                    { name: 'Note', value: 'An addition from the plugin.' }
                ]
            }
        }
    });
}
