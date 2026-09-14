var helper = require('./helper.js');

function translate(query, completion) {
    completion({
        result: {
            from: 'en',
            to: query.to,
            toParagraphs: [helper.shout(query.text)]
        }
    });
}
