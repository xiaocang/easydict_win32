function translate(query, completion) {
    var wrote = $file.write({ path: '$sandbox/note.txt', data: 'remembered: ' + query.text });
    var readBack = $file.read('$sandbox/note.txt');
    var packaged = $file.read('data.txt');
    var escaped = $file.write({ path: '../escaped.txt', data: 'nope' });

    completion({
        result: {
            from: 'en',
            to: query.to,
            toParagraphs: [
                'wrote=' + wrote,
                'readBack=' + (readBack ? readBack.toUTF8() : 'null'),
                'packaged=' + (packaged ? packaged.toUTF8().trim() : 'null'),
                'escaped=' + escaped
            ]
        }
    });
}
