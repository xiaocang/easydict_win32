use base64::Engine;
use easydict_app::compat_protocol::{
    ImportedMdxDictionarySnapshot, MdxLookupParams, SettingsSnapshot,
};
use easydict_app::{
    detect_mdx_file_encryption_mode, mdx_decode_base64_regcode, mdx_decrypt_block,
    mdx_decrypt_regcode_by_device_id, mdx_decrypt_regcode_by_email, mdx_fast_decrypt,
    mdx_ripemd128, mdx_salsa20_8, mime_type_for_mdd_resource_key, native_mdx_lookup_can_route,
    native_mdx_lookup_local_input_error, native_mdx_lookup_needs_credentials,
    native_mdx_lookup_requires_credential_bridge, normalize_mdd_resource_key,
    run_native_mdd_resource_lookup_with_factory, run_native_mdx_lookup,
    run_native_mdx_lookup_with_factories, run_native_mdx_lookup_with_factory, MdxEncryptionMode,
    NativeMddResourceError, NativeMddResourceReader, NativeMddResourceReaderFactory,
    NativeMdxDictionaryReader, NativeMdxDictionaryReaderFactory, NativeMdxLookupError,
};
use std::collections::{HashMap, VecDeque};
use std::fs;
use std::path::{Path, PathBuf};

#[test]
fn native_mdx_lookup_follows_link_redirects() {
    let settings = mdx_settings(false);
    let mut factory = RecordingMdxReaderFactory::with_readers([RecordingMdxReader::new(
        [
            ("apple", ("apple", "@@@LINK=fruit")),
            ("fruit", ("fruit", "<div>A fruit</div>")),
        ],
        [],
    )]);

    let result = run_native_mdx_lookup_with_factory(
        &mut factory,
        &MdxLookupParams {
            dictionary_id: "mdx::demo".to_string(),
            query: "apple".to_string(),
            fuzzy: false,
        },
        &settings,
    )
    .expect("native MDX lookup should succeed");

    assert_eq!(factory.opened, ["mdx::demo"]);
    assert_eq!(result.entries.len(), 1);
    assert_eq!(result.entries[0].key, "fruit");
    assert_eq!(result.entries[0].html, "<div>A fruit</div>");
    assert_eq!(
        result.entries[0].dictionary_name.as_deref(),
        Some("Demo Dictionary")
    );
}

#[test]
fn native_mdx_fuzzy_lookup_uses_candidate_keys() {
    let settings = mdx_settings(false);
    let mut factory = RecordingMdxReaderFactory::with_readers([RecordingMdxReader::new(
        [
            ("apple", ("apple", "<div>Apple</div>")),
            ("application", ("application", "<div>Application</div>")),
        ],
        ["apple", "application"],
    )]);

    let result = run_native_mdx_lookup_with_factory(
        &mut factory,
        &MdxLookupParams {
            dictionary_id: "mdx::demo".to_string(),
            query: "app".to_string(),
            fuzzy: true,
        },
        &settings,
    )
    .expect("native MDX fuzzy lookup should succeed");

    assert_eq!(
        result
            .entries
            .iter()
            .map(|entry| entry.key.as_str())
            .collect::<Vec<_>>(),
        ["apple", "application"]
    );
}

#[test]
fn native_mdx_route_is_limited_to_dictionaries_without_required_credentials() {
    let plain = mdx_settings(false);
    let encrypted_without_credentials = mdx_settings(true);
    let encrypted_with_credentials =
        mdx_settings_with_credentials(true, "reg", "email@example.com");
    let plain_with_stale_credentials =
        mdx_settings_with_credentials(false, "reg", "email@example.com");

    let params = MdxLookupParams {
        dictionary_id: "mdx::demo".to_string(),
        query: "apple".to_string(),
        fuzzy: false,
    };

    assert!(native_mdx_lookup_can_route(&params, &plain));
    assert!(native_mdx_lookup_can_route(
        &params,
        &plain_with_stale_credentials
    ));
    assert!(!native_mdx_lookup_can_route(
        &params,
        &encrypted_without_credentials
    ));
    assert!(!native_mdx_lookup_requires_credential_bridge(
        &params,
        &encrypted_without_credentials
    ));
    assert!(native_mdx_lookup_needs_credentials(
        &params,
        &encrypted_without_credentials
    ));
    assert!(!native_mdx_lookup_can_route(
        &params,
        &encrypted_with_credentials
    ));
    assert!(!native_mdx_lookup_requires_credential_bridge(
        &params,
        &encrypted_with_credentials
    ));
    assert!(!native_mdx_lookup_needs_credentials(
        &params,
        &encrypted_with_credentials
    ));
}

#[test]
fn native_mdx_key_info_encrypted_dictionary_routes_natively_without_credentials() {
    let temp_dir = unique_temp_dir("easydict-mdx-key-info-native");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Key Info Dictionary.mdx");
    write_mdx_header(
        &mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="2" />"#,
    );

    let mut dictionary = mdx_dictionary(true, []);
    dictionary.file_path = path_string(&mdx_path);
    dictionary.regcode = Some("not a base64 regcode".to_string());
    dictionary.email = Some("stale@example.com".to_string());
    let settings = mdx_settings_with_dictionary(dictionary);
    let params = MdxLookupParams {
        dictionary_id: "mdx::demo".to_string(),
        query: "apple".to_string(),
        fuzzy: false,
    };
    let mut factory = RecordingMdxReaderFactory::with_readers([RecordingMdxReader::new(
        [("apple", ("apple", "<div>Native key-info hit</div>"))],
        [],
    )]);

    assert_eq!(
        detect_mdx_file_encryption_mode(&mdx_path).unwrap(),
        MdxEncryptionMode::KeyInfoBlock
    );
    assert!(native_mdx_lookup_can_route(&params, &settings));
    assert!(!native_mdx_lookup_requires_credential_bridge(
        &params, &settings
    ));
    assert!(!native_mdx_lookup_needs_credentials(&params, &settings));
    assert!(native_mdx_lookup_local_input_error(&params, &settings).is_none());

    let result = run_native_mdx_lookup_with_factory(&mut factory, &params, &settings)
        .expect("key-info encrypted MDX should use the native reader");
    assert_eq!(factory.opened, ["mdx::demo"]);
    assert_eq!(result.entries[0].html, "<div>Native key-info hit</div>");

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn native_mdx_record_encrypted_dictionary_still_requires_credentials() {
    let temp_dir = unique_temp_dir("easydict-mdx-record-encrypted");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Secure Dictionary.mdx");
    write_mdx_header(
        &mdx_path,
        r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="1" RegisterBy="EMail" />"#,
    );

    let mut dictionary = mdx_dictionary(true, []);
    dictionary.file_path = path_string(&mdx_path);
    let settings = mdx_settings_with_dictionary(dictionary);
    let params = MdxLookupParams {
        dictionary_id: "mdx::demo".to_string(),
        query: "apple".to_string(),
        fuzzy: false,
    };

    assert_eq!(
        detect_mdx_file_encryption_mode(&mdx_path).unwrap(),
        MdxEncryptionMode::RecordBlock
    );
    assert!(!native_mdx_lookup_can_route(&params, &settings));
    assert!(native_mdx_lookup_needs_credentials(&params, &settings));
    assert!(!native_mdx_lookup_requires_credential_bridge(
        &params, &settings
    ));

    let settings =
        mdx_settings_with_credentials(true, "MDEyMzQ1Njc4OTo7PD0+Pw==", "email@example.com");
    let mut dictionary = settings
        .imported_mdx_dictionaries
        .as_ref()
        .expect("settings should include dictionary")[0]
        .clone();
    dictionary.file_path = path_string(&mdx_path);
    let settings = mdx_settings_with_dictionary(dictionary);

    assert!(native_mdx_lookup_can_route(&params, &settings));
    assert!(!native_mdx_lookup_requires_credential_bridge(
        &params, &settings
    ));
    assert!(!native_mdx_lookup_needs_credentials(&params, &settings));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn native_mdx_header_encryption_edge_values_fail_locally_without_bridge_boundary() {
    let temp_dir = unique_temp_dir("easydict-mdx-encryption-edge-values");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");

    let params = MdxLookupParams {
        dictionary_id: "mdx::demo".to_string(),
        query: "apple".to_string(),
        fuzzy: false,
    };

    for (name, header, expected_mode, expected_can_route, expected_local_error) in [
        (
            "single-quoted-yes",
            r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted='yes' RegisterBy='EMail' />"#,
            MdxEncryptionMode::RecordBlock,
            true,
            false,
        ),
        (
            "combined-3",
            r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="3" RegisterBy="EMail" />"#,
            MdxEncryptionMode::RecordAndKeyInfoBlock,
            false,
            true,
        ),
        (
            "unknown-value",
            r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="surprise" RegisterBy="EMail" />"#,
            MdxEncryptionMode::Unknown,
            false,
            true,
        ),
        (
            "missing-encrypted-attribute",
            r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" />"#,
            MdxEncryptionMode::None,
            true,
            false,
        ),
    ] {
        let mdx_path = temp_dir.join(format!("{name}.mdx"));
        write_mdx_header(&mdx_path, header);

        let mut dictionary = mdx_dictionary(expected_mode != MdxEncryptionMode::None, []);
        dictionary.file_path = path_string(&mdx_path);
        dictionary.regcode = Some("MDEyMzQ1Njc4OTo7PD0+Pw==".to_string());
        dictionary.email = Some("email@example.com".to_string());
        let settings = mdx_settings_with_dictionary(dictionary);

        assert_eq!(
            detect_mdx_file_encryption_mode(&mdx_path).unwrap(),
            expected_mode,
            "{name}"
        );
        assert_eq!(
            native_mdx_lookup_can_route(&params, &settings),
            expected_can_route,
            "{name}"
        );
        assert_eq!(
            native_mdx_lookup_requires_credential_bridge(&params, &settings),
            false,
            "{name}"
        );
        assert_eq!(
            native_mdx_lookup_local_input_error(&params, &settings)
                .map(|error| error
                    .to_string()
                    .contains("not supported by the Rust-native MDX reader"))
                .unwrap_or(false),
            expected_local_error,
            "{name}"
        );
    }

    let utf8_path = temp_dir.join("utf8-odd-header.mdx");
    let mut utf8_header =
        br#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="2" X="odd" />"#.to_vec();
    if utf8_header.len() % 2 == 0 {
        utf8_header.push(b' ');
    }
    write_raw_mdx_header(&utf8_path, &utf8_header);
    assert_eq!(
        detect_mdx_file_encryption_mode(&utf8_path).unwrap(),
        MdxEncryptionMode::KeyInfoBlock
    );

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn native_mdx_record_encrypted_dictionary_with_credentials_reads_natively() {
    let temp_dir = unique_temp_dir("easydict-mdx-record-encrypted-native");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Secure Dictionary.mdx");
    let regcode = "MDEyMzQ1Njc4OTo7PD0+Pw==";
    let email = "email@example.com";
    write_record_encrypted_mdx_fixture(&mdx_path, regcode, email);

    let mut dictionary = mdx_dictionary(true, []);
    dictionary.file_path = path_string(&mdx_path);
    dictionary.regcode = Some(regcode.to_string());
    dictionary.email = Some(email.to_string());
    let settings = mdx_settings_with_dictionary(dictionary);
    let params = MdxLookupParams {
        dictionary_id: "mdx::demo".to_string(),
        query: "apple".to_string(),
        fuzzy: false,
    };

    assert_eq!(
        detect_mdx_file_encryption_mode(&mdx_path).unwrap(),
        MdxEncryptionMode::RecordBlock
    );
    assert!(native_mdx_lookup_can_route(&params, &settings));
    assert!(!native_mdx_lookup_requires_credential_bridge(
        &params, &settings
    ));

    let result =
        run_native_mdx_lookup(&params, &settings).expect("encrypted fixture should read natively");
    assert_eq!(result.entries.len(), 1);
    assert_eq!(result.entries[0].key, "apple");
    assert_eq!(result.entries[0].html, "<div>Apple</div>");

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn native_mdx_record_encrypted_dictionary_with_device_id_credentials_reads_natively() {
    let temp_dir = unique_temp_dir("easydict-mdx-record-encrypted-device-native");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Device Dictionary.mdx");
    let regcode = "MDEyMzQ1Njc4OTo7PD0+Pw==";
    let device_id = "device-123";
    write_record_encrypted_mdx_fixture_with_register_by(&mdx_path, regcode, "DeviceID", device_id);

    let mut dictionary = mdx_dictionary(true, []);
    dictionary.file_path = path_string(&mdx_path);
    dictionary.regcode = Some(regcode.to_string());
    dictionary.email = Some(device_id.to_string());
    let settings = mdx_settings_with_dictionary(dictionary);
    let params = MdxLookupParams {
        dictionary_id: "mdx::demo".to_string(),
        query: "apple".to_string(),
        fuzzy: false,
    };

    assert_eq!(
        detect_mdx_file_encryption_mode(&mdx_path).unwrap(),
        MdxEncryptionMode::RecordBlock
    );
    assert!(native_mdx_lookup_can_route(&params, &settings));
    assert!(!native_mdx_lookup_requires_credential_bridge(
        &params, &settings
    ));

    let result = run_native_mdx_lookup(&params, &settings)
        .expect("device-id encrypted fixture should read natively");
    assert_eq!(result.entries.len(), 1);
    assert_eq!(result.entries[0].key, "apple");
    assert_eq!(result.entries[0].html, "<div>Apple</div>");

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn native_mdx_lookup_reports_missing_credentials_without_opening_reader() {
    let settings = mdx_settings(true);
    let mut factory = RecordingMdxReaderFactory::default();

    let error = run_native_mdx_lookup_with_factory(
        &mut factory,
        &MdxLookupParams {
            dictionary_id: "mdx::demo".to_string(),
            query: "apple".to_string(),
            fuzzy: false,
        },
        &settings,
    )
    .expect_err("encrypted MDX without credentials should fail locally");

    assert!(error.to_string().contains("credentials are required"));
    assert!(factory.opened.is_empty());
}

#[test]
fn native_mdx_lookup_reports_missing_encrypted_file_before_credential_bridge() {
    let settings = mdx_settings_with_credentials(true, "reg", "email@example.com");
    let mut factory = RecordingMdxReaderFactory::default();
    let params = MdxLookupParams {
        dictionary_id: "mdx::demo".to_string(),
        query: "apple".to_string(),
        fuzzy: false,
    };

    let input_error = native_mdx_lookup_local_input_error(&params, &settings)
        .expect("missing file should be a local input error");
    assert!(input_error.to_string().contains("file not found"));

    let error = run_native_mdx_lookup_with_factory(&mut factory, &params, &settings)
        .expect_err("missing encrypted MDX file should fail locally");

    assert!(error.to_string().contains("file not found"));
    assert!(factory.opened.is_empty());
}

#[test]
fn native_mdx_lookup_reports_invalid_encrypted_regcode_without_opening_reader() {
    let temp_dir = unique_temp_dir("easydict-mdx-invalid-regcode");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Secure Dictionary.mdx");
    fs::write(&mdx_path, b"test mdx").expect("test MDX file should be written");

    let mut dictionary = mdx_dictionary(true, []);
    dictionary.file_path = path_string(&mdx_path);
    dictionary.regcode = Some("not a base64 regcode".to_string());
    dictionary.email = Some("email@example.com".to_string());
    let settings = mdx_settings_with_dictionary(dictionary);
    let mut factory = RecordingMdxReaderFactory::default();
    let params = MdxLookupParams {
        dictionary_id: "mdx::demo".to_string(),
        query: "apple".to_string(),
        fuzzy: false,
    };

    let input_error = native_mdx_lookup_local_input_error(&params, &settings)
        .expect("invalid regcode should be a local input error");
    assert!(input_error.to_string().contains("Base64"));

    let error = run_native_mdx_lookup_with_factory(&mut factory, &params, &settings)
        .expect_err("invalid encrypted MDX regcode should fail locally");

    assert!(error.to_string().contains("Base64"));
    assert!(factory.opened.is_empty());

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn native_mdx_lookup_reports_invalid_encrypted_regcode_key_length_locally() {
    let temp_dir = unique_temp_dir("easydict-mdx-invalid-regcode-length");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Secure Dictionary.mdx");
    fs::write(&mdx_path, b"test mdx").expect("test MDX file should be written");
    let short_regcode = base64::engine::general_purpose::STANDARD.encode([0x30; 8]);

    let mut dictionary = mdx_dictionary(true, []);
    dictionary.file_path = path_string(&mdx_path);
    dictionary.regcode = Some(short_regcode);
    dictionary.email = Some("email@example.com".to_string());
    let settings = mdx_settings_with_dictionary(dictionary);
    let params = MdxLookupParams {
        dictionary_id: "mdx::demo".to_string(),
        query: "apple".to_string(),
        fuzzy: false,
    };

    let input_error = native_mdx_lookup_local_input_error(&params, &settings)
        .expect("invalid regcode length should be a local input error");

    assert!(input_error.to_string().contains("16 or 32 byte"));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn native_mdx_missing_dictionary_is_empty_result() {
    let settings = mdx_settings(false);
    let mut factory = RecordingMdxReaderFactory::default();

    let result = run_native_mdx_lookup_with_factory(
        &mut factory,
        &MdxLookupParams {
            dictionary_id: "mdx::missing".to_string(),
            query: "apple".to_string(),
            fuzzy: false,
        },
        &settings,
    )
    .expect("missing dictionary mirrors CompatHost empty result");

    assert!(result.entries.is_empty());
    assert!(factory.opened.is_empty());
}

#[test]
fn native_mdd_resource_lookup_normalizes_keys_and_uses_first_hit() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\demo.mdd", r"C:\Dicts\demo.1.mdd"]);
    let mut factory = RecordingMddReaderFactory::with_readers([
        Ok(RecordingMddReader::new([])),
        Ok(RecordingMddReader::new([(
            r"\images\logo.png",
            b"\x89PNG".as_slice(),
        )])),
    ]);

    let resource =
        run_native_mdd_resource_lookup_with_factory(&mut factory, &dictionary, "images/logo.png")
            .expect("MDD resource lookup should not fail")
            .expect("second MDD should contain the resource");

    assert_eq!(
        factory.opened,
        [r"C:\Dicts\demo.mdd", r"C:\Dicts\demo.1.mdd"]
    );
    assert_eq!(resource.key, r"\images\logo.png");
    assert_eq!(resource.mime_type, "image/png");
    assert_eq!(resource.data, [0x89, b'P', b'N', b'G']);
}

#[test]
fn native_mdd_resource_lookup_skips_failed_mdd_files() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\missing.mdd", r"C:\Dicts\demo.mdd"]);
    let mut factory = RecordingMddReaderFactory::with_readers([
        Err(NativeMddResourceError::new("missing test MDD")),
        Ok(RecordingMddReader::new([(
            r"\styles\dict.css",
            b"body{}".as_slice(),
        )])),
    ]);

    let resource =
        run_native_mdd_resource_lookup_with_factory(&mut factory, &dictionary, "styles/dict.css")
            .expect("failed MDD files should be skipped")
            .expect("second MDD should be checked after a failed first file");

    assert_eq!(resource.mime_type, "text/css");
    assert_eq!(resource.data, b"body{}");
}

#[test]
fn native_mdd_resource_lookup_returns_none_without_mdd_paths_or_hits() {
    let dictionary = mdx_dictionary(false, []);
    let mut factory = RecordingMddReaderFactory::default();

    let resource =
        run_native_mdd_resource_lookup_with_factory(&mut factory, &dictionary, "missing.png")
            .expect("empty MDD path list should be a neutral miss");

    assert_eq!(resource, None);
    assert!(factory.opened.is_empty());
}

#[test]
fn native_mdx_lookup_inlines_mdd_resources_into_webview_ready_html() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\demo.mdd", r"C:\Dicts\demo.1.mdd"]);
    let settings = mdx_settings_with_dictionary(dictionary);
    let mut mdx_factory = RecordingMdxReaderFactory::with_readers([RecordingMdxReader::new(
        [(
            "apple",
            (
                "apple",
                r#"<div>
                    <img src="images/logo.png">
                    <link href='styles/dict.css'>
                    <audio src="https://dictassets/audio/pron.mp3"></audio>
                    <span style="background-image:url(images/bg.webp)"></span>
                    <a href="https://example.com/keep">external</a>
                    <img src="data:image/png;base64,OLD">
                    <a href="javascript:alert(1)">script</a>
                </div>"#,
            ),
        )],
        [],
    )]);
    let mut mdd_factory = RecordingMddReaderFactory::with_readers([
        Ok(RecordingMddReader::new([])),
        Ok(RecordingMddReader::new([
            (r"\images\logo.png", b"\x89PNG".as_slice()),
            (r"\styles\dict.css", b"body{}".as_slice()),
            (r"\audio\pron.mp3", b"ID3".as_slice()),
            (r"\images\bg.webp", b"RIFF".as_slice()),
        ])),
    ]);

    let result = run_native_mdx_lookup_with_factories(
        &mut mdx_factory,
        &mut mdd_factory,
        &MdxLookupParams {
            dictionary_id: "mdx::demo".to_string(),
            query: "apple".to_string(),
            fuzzy: false,
        },
        &settings,
    )
    .expect("MDX lookup should inline MDD resources");

    assert_eq!(
        mdd_factory.opened,
        [r"C:\Dicts\demo.mdd", r"C:\Dicts\demo.1.mdd"]
    );
    let html = &result.entries[0].html;
    assert!(html.contains(r#"src="data:image/png;base64,iVBORw==""#));
    assert!(html.contains("href='data:text/css;base64,Ym9keXt9'"));
    assert!(html.contains(r#"src="data:audio/mpeg;base64,SUQz""#));
    assert!(html.contains("url('data:image/webp;base64,UklGRg==')"));
    assert!(html.contains(r#"href="https://example.com/keep""#));
    assert!(html.contains(r#"src="data:image/png;base64,OLD""#));
    assert!(html.contains(r#"href="javascript:alert(1)""#));
    assert!(!html.contains("https://dictassets/audio/pron.mp3"));
}

#[test]
fn mdd_resource_key_and_mime_helpers_match_dictionary_resource_contract() {
    assert_eq!(
        normalize_mdd_resource_key("images/logo.JPG").unwrap(),
        r"\images\logo.JPG"
    );
    assert_eq!(
        normalize_mdd_resource_key(r"\audio\hello.mp3").unwrap(),
        r"\audio\hello.mp3"
    );
    assert!(normalize_mdd_resource_key("  ").is_err());

    assert_eq!(
        mime_type_for_mdd_resource_key(r"\images\logo.JPG"),
        "image/jpeg"
    );
    assert_eq!(
        mime_type_for_mdd_resource_key(r"\fonts\dict.woff2"),
        "font/woff2"
    );
    assert_eq!(
        mime_type_for_mdd_resource_key(r"\unknown\payload.bin"),
        "application/octet-stream"
    );
}

#[test]
fn mdx_encryption_ripemd128_matches_mdict_csharp_vectors() {
    assert_eq!(
        mdx_ripemd128(&[]),
        hex_16("cdf26213a150dc3ecb610f18f6b38b46")
    );
    assert_eq!(
        mdx_ripemd128(b"abc"),
        hex_16("c14a12199c66e4ba84636b0f69144c77")
    );
    assert_eq!(
        mdx_ripemd128(&utf16_le("user@example.com")),
        hex_16("c821d19d2ccb34e07f82d3365a4dea6b")
    );
}

#[test]
fn mdx_encryption_salsa20_8_matches_bouncycastle_vectors() {
    let data40 = data40();
    let key16 = sequence(0, 16);
    let key32 = sequence(0, 32);

    assert_eq!(
        mdx_salsa20_8(&data40, &key16).unwrap(),
        hex_bytes(
            "be4ef465e9594e9e93f1b0c47b0c75f3dbb6f880784e1c7a68405c6254602c1bca4f5740dd50bfd8"
        )
    );
    assert_eq!(
        mdx_salsa20_8(&data40, &key32).unwrap(),
        hex_bytes(
            "bd35bb92f890ca7b36a3f1ffe8b5980e518db05dc53cc7baf2c776909167ab801d66b2116bd9c384"
        )
    );
}

#[test]
fn mdx_encryption_regcode_derivation_matches_mdict_csharp_vectors() {
    let regcode = sequence(0x30, 16);
    let email_key = mdx_decrypt_regcode_by_email(&regcode, "user@example.com")
        .expect("email regcode derivation should succeed");

    assert_eq!(email_key, hex_bytes("ffdde4227ccb84b54045bebc624094a8"));
    assert_eq!(
        mdx_salsa20_8(&data40(), &email_key).unwrap(),
        hex_bytes(
            "9ecbbf69513627ac3d199e9307602e78835d878df13cb19714903d3a7a9ba3642546f25fbab02f01"
        )
    );
    assert_eq!(
        mdx_decrypt_regcode_by_device_id(&regcode, b"device-123").unwrap(),
        hex_bytes("0177bda31e40f234302910668d37872f")
    );
}

#[test]
fn mdx_encryption_regcode_base64_and_key_validation_are_local_errors() {
    let regcode = sequence(0x30, 16);
    let encoded = base64::engine::general_purpose::STANDARD.encode(&regcode);
    assert_eq!(mdx_decode_base64_regcode(&encoded).unwrap(), regcode);

    let invalid_regcode =
        mdx_decode_base64_regcode("not a base64 regcode").expect_err("invalid base64 should fail");
    assert!(invalid_regcode.to_string().contains("Base64"));

    let invalid_key = vec![0; 15];
    let invalid_salsa =
        mdx_salsa20_8(&[1, 2, 3], &invalid_key).expect_err("invalid key should fail locally");
    assert!(invalid_salsa.to_string().contains("16 or 32 bytes"));
}

#[test]
fn mdx_encryption_fast_decrypt_matches_mdict_csharp_vector() {
    let data = (0..32)
        .map(|index| (index * 7 + 3) as u8)
        .collect::<Vec<_>>();
    let key = sequence(0, 16);

    assert_eq!(
        mdx_fast_decrypt(&data, &key).unwrap(),
        hex_bytes("06a31b90e97df46e871fd64c25b208a34bc4721960f65fc70e9039a5ac3b8308")
    );

    let error = mdx_fast_decrypt(&data, &[]).expect_err("empty key should fail locally");
    assert!(error.to_string().contains("key cannot be empty"));
}

#[test]
fn mdx_encryption_block_decrypt_matches_mdict_csharp_vector() {
    let mut block = vec![0x02, 0x00, 0x00, 0x00, 0xaa, 0xbb, 0xcc, 0xdd];
    block.extend((0..24).map(|index| (index * 5 + 1) as u8));

    assert_eq!(
        mdx_decrypt_block(&block).unwrap(),
        hex_bytes("02000000aabbccdd7acd87a6cc5d25c79f341d8311df5af21588c2e1ab3a42a2")
    );

    let error = mdx_decrypt_block(&block[..7]).expect_err("short block should fail locally");
    assert!(error.to_string().contains("at least 8 bytes"));
}

fn mdx_settings(is_encrypted: bool) -> SettingsSnapshot {
    mdx_settings_with_dictionary(mdx_dictionary(is_encrypted, []))
}

fn mdx_settings_with_credentials(
    is_encrypted: bool,
    regcode: &str,
    email: &str,
) -> SettingsSnapshot {
    let mut dictionary = mdx_dictionary(is_encrypted, []);
    dictionary.regcode = Some(regcode.to_string());
    dictionary.email = Some(email.to_string());
    mdx_settings_with_dictionary(dictionary)
}

fn mdx_settings_with_dictionary(dictionary: ImportedMdxDictionarySnapshot) -> SettingsSnapshot {
    SettingsSnapshot {
        imported_mdx_dictionaries: Some(vec![dictionary]),
        ..SettingsSnapshot::default()
    }
}

fn mdx_dictionary<const N: usize>(
    is_encrypted: bool,
    mdd_paths: [&str; N],
) -> ImportedMdxDictionarySnapshot {
    ImportedMdxDictionarySnapshot {
        service_id: "mdx::demo".to_string(),
        display_name: "Demo Dictionary".to_string(),
        file_path: r"C:\Dicts\demo.mdx".to_string(),
        is_encrypted,
        regcode: None,
        email: None,
        mdd_file_paths: mdd_paths.into_iter().map(str::to_string).collect(),
    }
}

fn unique_temp_dir(prefix: &str) -> PathBuf {
    let mut path = std::env::temp_dir();
    path.push(format!(
        "{}-{}-{}",
        prefix,
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("system time should be after Unix epoch")
            .as_nanos()
    ));
    path
}

fn path_string(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

fn write_mdx_header(path: &Path, header: &str) {
    let mut header_bytes = Vec::new();
    for code_unit in header.encode_utf16() {
        header_bytes.extend_from_slice(&code_unit.to_le_bytes());
    }

    let mut file_bytes = Vec::new();
    file_bytes.extend_from_slice(&(header_bytes.len() as u32).to_be_bytes());
    file_bytes.extend_from_slice(&header_bytes);
    file_bytes.extend_from_slice(&0u32.to_be_bytes());
    fs::write(path, file_bytes).expect("MDX header should be written");
}

fn write_raw_mdx_header(path: &Path, header_bytes: &[u8]) {
    let mut file_bytes = Vec::new();
    file_bytes.extend_from_slice(&(header_bytes.len() as u32).to_be_bytes());
    file_bytes.extend_from_slice(header_bytes);
    file_bytes.extend_from_slice(&0u32.to_be_bytes());
    fs::write(path, file_bytes).expect("raw MDX header should be written");
}

fn write_record_encrypted_mdx_fixture(path: &Path, regcode: &str, email: &str) {
    write_record_encrypted_mdx_fixture_with_register_by(path, regcode, "EMail", email);
}

fn write_record_encrypted_mdx_fixture_with_register_by(
    path: &Path,
    regcode: &str,
    register_by: &str,
    user_id: &str,
) {
    let header = format!(
        r#"<Dictionary GeneratedByEngineVersion="1.2" RequiredEngineVersion="1.2" Encoding="UTF-8" Encrypted="1" RegisterBy="{register_by}" />"#
    );
    let mut header_bytes = Vec::new();
    for code_unit in header.encode_utf16() {
        header_bytes.extend_from_slice(&code_unit.to_le_bytes());
    }

    let key_block_unpacked = {
        let mut bytes = Vec::new();
        push_u32_be(&mut bytes, 0);
        bytes.extend_from_slice(b"apple\0");
        bytes
    };
    let key_block = mdx_none_block(&key_block_unpacked);

    let key_info = {
        let mut bytes = Vec::new();
        push_u32_be(&mut bytes, 1);
        bytes.push(5);
        bytes.extend_from_slice(b"apple");
        bytes.push(5);
        bytes.extend_from_slice(b"apple");
        push_u32_be(&mut bytes, key_block.len() as u32);
        push_u32_be(&mut bytes, key_block_unpacked.len() as u32);
        bytes
    };

    let compressed_definition = hex_bytes("789cb349c92cb3732c28c849b5d10731012fd6059c");
    let record_block_plain = mdx_zlib_block(&compressed_definition);
    let record_block = mdx_encrypt_block(&record_block_plain);
    assert_eq!(
        mdx_decrypt_block(&record_block).expect("encrypted record block should decrypt"),
        record_block_plain
    );

    let mut key_header = Vec::new();
    push_u32_be(&mut key_header, 1);
    push_u32_be(&mut key_header, 1);
    push_u32_be(&mut key_header, key_info.len() as u32);
    push_u32_be(&mut key_header, key_block.len() as u32);

    let regcode = mdx_decode_base64_regcode(regcode).expect("test regcode should be valid");
    let key = if register_by.eq_ignore_ascii_case("EMail") {
        mdx_decrypt_regcode_by_email(&regcode, user_id)
            .expect("test email regcode should derive key header key")
    } else {
        mdx_decrypt_regcode_by_device_id(&regcode, user_id.as_bytes())
            .expect("test device regcode should derive key header key")
    };
    let encrypted_key_header = mdx_salsa20_8(&key_header, &key).expect("key header should encrypt");

    let mut record_header = Vec::new();
    push_u32_be(&mut record_header, 1);
    push_u32_be(&mut record_header, 1);
    push_u32_be(&mut record_header, 8);
    push_u32_be(&mut record_header, record_block.len() as u32);

    let mut record_info = Vec::new();
    push_u32_be(&mut record_info, record_block.len() as u32);
    push_u32_be(&mut record_info, "<div>Apple</div>".len() as u32);

    let mut file_bytes = Vec::new();
    file_bytes.extend_from_slice(&(header_bytes.len() as u32).to_be_bytes());
    file_bytes.extend_from_slice(&header_bytes);
    file_bytes.extend_from_slice(&0u32.to_be_bytes());
    file_bytes.extend_from_slice(&encrypted_key_header);
    file_bytes.extend_from_slice(&key_info);
    file_bytes.extend_from_slice(&key_block);
    file_bytes.extend_from_slice(&record_header);
    file_bytes.extend_from_slice(&record_info);
    file_bytes.extend_from_slice(&record_block);
    fs::write(path, file_bytes).expect("encrypted MDX fixture should be written");
}

fn mdx_none_block(payload: &[u8]) -> Vec<u8> {
    let mut block = vec![0, 0, 0, 0, 0, 0, 0, 0];
    block.extend_from_slice(payload);
    block
}

fn mdx_zlib_block(compressed_payload: &[u8]) -> Vec<u8> {
    let mut block = vec![2, 0, 0, 0, 0x11, 0x22, 0x33, 0x44];
    block.extend_from_slice(compressed_payload);
    block
}

fn mdx_encrypt_block(plain_block: &[u8]) -> Vec<u8> {
    let mut key_input = [0u8; 8];
    key_input[..4].copy_from_slice(&plain_block[4..8]);
    key_input[4] ^= 0x95;
    key_input[5] ^= 0x36;
    let key = mdx_ripemd128(&key_input);

    let mut encrypted = Vec::with_capacity(plain_block.len());
    encrypted.extend_from_slice(&plain_block[..8]);
    encrypted.extend_from_slice(&mdx_fast_encrypt(&plain_block[8..], &key));
    encrypted
}

fn mdx_fast_encrypt(data: &[u8], key: &[u8]) -> Vec<u8> {
    let mut output = Vec::with_capacity(data.len());
    let mut previous = 0x36u8;
    for (index, byte) in data.iter().enumerate() {
        let encrypted =
            (byte ^ previous ^ ((index & 0xff) as u8) ^ key[index % key.len()]).rotate_right(4);
        previous = encrypted;
        output.push(encrypted);
    }
    output
}

fn push_u32_be(bytes: &mut Vec<u8>, value: u32) {
    bytes.extend_from_slice(&value.to_be_bytes());
}

fn data40() -> Vec<u8> {
    (0..40).map(|index| (index * 3 + 7) as u8).collect()
}

fn sequence(start: u8, len: usize) -> Vec<u8> {
    (0..len)
        .map(|index| start.wrapping_add(index as u8))
        .collect()
}

fn utf16_le(value: &str) -> Vec<u8> {
    value
        .encode_utf16()
        .flat_map(u16::to_le_bytes)
        .collect::<Vec<_>>()
}

fn hex_16(value: &str) -> [u8; 16] {
    let bytes = hex_bytes(value);
    bytes
        .try_into()
        .expect("test vector should contain 16 bytes")
}

fn hex_bytes(value: &str) -> Vec<u8> {
    assert!(value.len() % 2 == 0);
    value
        .as_bytes()
        .chunks_exact(2)
        .map(|chunk| {
            let high = hex_nibble(chunk[0]);
            let low = hex_nibble(chunk[1]);
            high << 4 | low
        })
        .collect()
}

fn hex_nibble(byte: u8) -> u8 {
    match byte {
        b'0'..=b'9' => byte - b'0',
        b'a'..=b'f' => byte - b'a' + 10,
        b'A'..=b'F' => byte - b'A' + 10,
        _ => panic!("invalid hex test vector"),
    }
}

#[derive(Default)]
struct RecordingMdxReaderFactory {
    opened: Vec<String>,
    readers: VecDeque<RecordingMdxReader>,
}

impl RecordingMdxReaderFactory {
    fn with_readers(readers: impl IntoIterator<Item = RecordingMdxReader>) -> Self {
        Self {
            opened: Vec::new(),
            readers: readers.into_iter().collect(),
        }
    }
}

impl NativeMdxDictionaryReaderFactory for RecordingMdxReaderFactory {
    type Reader = RecordingMdxReader;

    fn open(
        &mut self,
        dictionary: &ImportedMdxDictionarySnapshot,
    ) -> Result<Self::Reader, NativeMdxLookupError> {
        self.opened.push(dictionary.service_id.clone());
        self.readers
            .pop_front()
            .ok_or_else(|| NativeMdxLookupError::new("test reader was not queued"))
    }
}

struct RecordingMdxReader {
    entries: HashMap<String, (String, String)>,
    fuzzy_keys: Vec<String>,
}

impl RecordingMdxReader {
    fn new<const N: usize, const M: usize>(
        entries: [(&str, (&str, &str)); N],
        fuzzy_keys: [&str; M],
    ) -> Self {
        Self {
            entries: entries
                .into_iter()
                .map(|(query, (key, definition))| {
                    (query.to_string(), (key.to_string(), definition.to_string()))
                })
                .collect(),
            fuzzy_keys: fuzzy_keys.into_iter().map(str::to_string).collect(),
        }
    }
}

impl NativeMdxDictionaryReader for RecordingMdxReader {
    fn lookup(&mut self, query: &str) -> Result<Option<(String, String)>, NativeMdxLookupError> {
        Ok(self.entries.get(query).cloned())
    }

    fn all_keys(&mut self) -> Result<Vec<String>, NativeMdxLookupError> {
        Ok(self.fuzzy_keys.clone())
    }

    fn fuzzy_keys(
        &mut self,
        query: &str,
        max_results: usize,
        max_distance: usize,
    ) -> Result<Vec<String>, NativeMdxLookupError> {
        assert_eq!(query, "app");
        assert_eq!(max_results, 20);
        assert_eq!(max_distance, 3);
        Ok(self.fuzzy_keys.clone())
    }
}

#[derive(Default)]
struct RecordingMddReaderFactory {
    opened: Vec<String>,
    readers: VecDeque<Result<RecordingMddReader, NativeMddResourceError>>,
}

impl RecordingMddReaderFactory {
    fn with_readers(
        readers: impl IntoIterator<Item = Result<RecordingMddReader, NativeMddResourceError>>,
    ) -> Self {
        Self {
            opened: Vec::new(),
            readers: readers.into_iter().collect(),
        }
    }
}

impl NativeMddResourceReaderFactory for RecordingMddReaderFactory {
    type Reader = RecordingMddReader;

    fn open_mdd(&mut self, path: &str) -> Result<Self::Reader, NativeMddResourceError> {
        self.opened.push(path.to_string());
        self.readers
            .pop_front()
            .ok_or_else(|| NativeMddResourceError::new("test MDD reader was not queued"))?
    }
}

struct RecordingMddReader {
    resources: HashMap<String, Vec<u8>>,
}

impl RecordingMddReader {
    fn new(resources: impl IntoIterator<Item = (&'static str, &'static [u8])>) -> Self {
        Self {
            resources: resources
                .into_iter()
                .map(|(key, data)| (key.to_string(), data.to_vec()))
                .collect(),
        }
    }
}

impl NativeMddResourceReader for RecordingMddReader {
    fn locate_raw(
        &mut self,
        resource_key: &str,
    ) -> Result<Option<(String, Vec<u8>)>, NativeMddResourceError> {
        Ok(self
            .resources
            .get(resource_key)
            .cloned()
            .map(|data| (resource_key.to_string(), data)))
    }
}
