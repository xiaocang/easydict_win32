use base64::Engine;
use easydict_app::protocol::{ImportedMdxDictionarySnapshot, MdxLookupParams, SettingsSnapshot};
use easydict_app::{
    detect_mdx_file_encryption_mode, discover_mdd_file_paths, inline_mdd_resources_in_html,
    inline_mdd_resources_in_html_with_factory, mdx_decode_base64_regcode, mdx_decrypt_block,
    mdx_decrypt_regcode_by_device_id, mdx_decrypt_regcode_by_email, mdx_fast_decrypt,
    mdx_ripemd128, mdx_salsa20_8, mime_type_for_mdd_resource_key, native_mdx_lookup_can_route,
    native_mdx_lookup_local_input_error, native_mdx_lookup_needs_credentials,
    normalize_mdd_resource_key, run_native_mdd_resource_lookup,
    run_native_mdd_resource_lookup_with_factory, run_native_mdx_lookup,
    run_native_mdx_lookup_with_factories, run_native_mdx_lookup_with_factories_and_mdd_policy,
    run_native_mdx_lookup_with_factory, MdxEncryptionMode, NativeMddResourceError,
    NativeMddResourceReader, NativeMddResourceReaderFactory, NativeMdxDictionaryReader,
    NativeMdxDictionaryReaderFactory, NativeMdxLookupError, RsMdictMddReaderFactory,
};
use flate2::{write::ZlibEncoder, Compression};
use std::collections::{HashMap, VecDeque};
use std::fs;
use std::io::Write;
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
    assert!(native_mdx_lookup_needs_credentials(
        &params,
        &encrypted_without_credentials
    ));
    assert!(!native_mdx_lookup_can_route(
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
    assert!(!native_mdx_lookup_needs_credentials(&params, &settings));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn native_mdx_header_encryption_edge_values_classify_rust_native_boundaries() {
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
            native_mdx_lookup_local_input_error(&params, &settings)
                .map(|error| error
                    .to_string()
                    .contains("not supported by the Rust-native MDX reader"))
                .unwrap_or(false),
            expected_local_error,
            "{name}"
        );

        if expected_local_error {
            let mut dictionary = mdx_dictionary(true, []);
            dictionary.file_path = path_string(&mdx_path);
            let settings = mdx_settings_with_dictionary(dictionary);
            assert!(
                !native_mdx_lookup_needs_credentials(&params, &settings),
                "{name} should be unsupported, not credential-gated"
            );
            let error = native_mdx_lookup_local_input_error(&params, &settings)
                .expect("unsupported encryption should fail locally without credentials");
            assert!(
                error
                    .to_string()
                    .contains("not supported by the Rust-native MDX reader"),
                "{name}: {error}"
            );
            let mut factory = RecordingMdxReaderFactory::default();
            let lookup_error = run_native_mdx_lookup_with_factory(&mut factory, &params, &settings)
                .expect_err("unsupported encryption should fail before opening MDX reader");
            assert!(
                lookup_error
                    .to_string()
                    .contains("not supported by the Rust-native MDX reader"),
                "{name}: {lookup_error}"
            );
            assert!(
                factory.opened.is_empty(),
                "{name} should not open the MDX reader after unsupported encryption preflight"
            );
        }
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
fn native_mdx_lookup_reports_missing_encrypted_file_before_opening_reader() {
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
    .expect("missing dictionary should remain a neutral empty result");

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
fn native_mdd_resource_lookup_skips_mdd_lookup_errors() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\bad.mdd", r"C:\Dicts\demo.mdd"]);
    let mut factory = RecordingMddReaderFactory::with_readers([
        Ok(RecordingMddReader::failing_lookup("bad record block")),
        Ok(RecordingMddReader::new([(
            r"\styles\dict.css",
            b"body{}".as_slice(),
        )])),
    ]);

    let resource =
        run_native_mdd_resource_lookup_with_factory(&mut factory, &dictionary, "styles/dict.css")
            .expect("failed MDD lookup should be skipped")
            .expect("second MDD should be checked after a failed lookup");

    assert_eq!(resource.key, r"\styles\dict.css");
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
fn native_mdd_resource_lookup_treats_empty_key_as_neutral_miss() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\demo.mdd"]);
    let mut factory = RecordingMddReaderFactory::with_readers([Ok(RecordingMddReader::new([(
        r"\images\logo.png",
        b"\x89PNG".as_slice(),
    )]))]);

    let resource = run_native_mdd_resource_lookup_with_factory(&mut factory, &dictionary, "   ")
        .expect("empty direct resource key should be a neutral miss");

    assert_eq!(resource, None);
    assert!(factory.opened.is_empty());
}

#[test]
fn native_mdd_resource_lookup_reads_real_rs_mdict_mdd_fixture() {
    let temp_dir = unique_temp_dir("easydict-native-mdd-real-fixture");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdd_path = temp_dir.join("Demo.mdd");
    write_minimal_mdd_fixture(
        &mdd_path,
        &[
            (r"\images\logo.png", b"\x89PNG".as_slice()),
            (r"\styles\dict.css", b"body{}".as_slice()),
        ],
    );

    let mut dictionary = mdx_dictionary(false, []);
    dictionary.mdd_file_paths = vec![path_string(&mdd_path)];

    let css = run_native_mdd_resource_lookup(&dictionary, "styles/dict.css")
        .expect("real MDD lookup should not fail")
        .expect("CSS resource should exist");
    assert_eq!(css.key, r"\styles\dict.css");
    assert_eq!(css.mime_type, "text/css");
    assert_eq!(css.data, b"body{}");

    let logo = run_native_mdd_resource_lookup(&dictionary, "/images/logo.png")
        .expect("real MDD lookup should normalize slash-prefixed keys")
        .expect("PNG resource should exist");
    assert_eq!(logo.key, r"\images\logo.png");
    assert_eq!(logo.mime_type, "image/png");
    assert_eq!(logo.data, b"\x89PNG");

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn native_mdd_resource_lookup_reads_record_encrypted_mdd_with_dictionary_credentials() {
    let temp_dir = unique_temp_dir("easydict-native-mdd-encrypted-real-fixture");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Secure Demo.mdx");
    let mdd_path = temp_dir.join("Secure Demo.mdd");
    let regcode = "MDEyMzQ1Njc4OTo7PD0+Pw==";
    let email = "email@example.com";
    write_record_encrypted_mdx_fixture(&mdx_path, regcode, email);
    write_record_encrypted_mdd_fixture(
        &mdd_path,
        regcode,
        email,
        &[(r"\images\secret.png", b"\x89PNG".as_slice())],
    );

    let mut dictionary = mdx_dictionary(true, []);
    dictionary.file_path = path_string(&mdx_path);
    dictionary.regcode = Some(regcode.to_string());
    dictionary.email = Some(email.to_string());
    dictionary.mdd_file_paths = vec![path_string(&mdd_path)];

    let image = run_native_mdd_resource_lookup(&dictionary, "images/secret.png")
        .expect("encrypted MDD lookup should not fail")
        .expect("encrypted MDD resource should exist");
    assert_eq!(image.key, r"\images\secret.png");
    assert_eq!(image.mime_type, "image/png");
    assert_eq!(image.data, b"\x89PNG");

    let html = inline_mdd_resources_in_html(&dictionary, r#"<img src="images/secret.png">"#)
        .expect("encrypted MDD HTML inline should not fail");
    assert!(html.contains(r#"src="data:image/png;base64,iVBORw==""#));

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
}

#[test]
fn native_mdd_resource_lookup_reads_real_corpus_resource_inventory_from_env() {
    let Some(mdd_path) = real_corpus_path("RS_MDICT_TEST_MDD") else {
        return;
    };

    let mut mdd = rust_mdict::Mdd::new(&mdd_path).expect("real corpus MDD should open natively");
    let resource_keys = mdd
        .resource_keys()
        .into_iter()
        .map(str::to_string)
        .collect::<Vec<_>>();

    assert_eq!(mdd.resource_count(), resource_keys.len());
    assert_eq!(
        resource_keys,
        vec![r"\cceu.css".to_string()],
        "Collins COBUILD English Usage MDD should currently expose only its stylesheet resource"
    );

    let css = mdd
        .locate_resource_result("cceu.css")
        .expect("real corpus CSS lookup should not fail")
        .expect("real corpus CSS resource should exist");
    assert_eq!(css.key, r"\cceu.css");
    assert_eq!(css.extension, "css");
    assert_eq!(css.mime_type, "text/css");
    let css_text = String::from_utf8(css.data.clone()).expect("real corpus CSS should be UTF-8");
    assert!(css_text.contains("box-sizing"));

    let mut dictionary = mdx_dictionary(false, []);
    dictionary.mdd_file_paths = vec![mdd_path];
    for resource_key in [
        "cceu.css",
        "/cceu.css",
        r"\cceu.css",
        "./cceu.css",
        r".\cceu.css",
    ] {
        let app_resource = run_native_mdd_resource_lookup(&dictionary, resource_key)
            .unwrap_or_else(|error| panic!("app MDD facade should read {resource_key}: {error}"))
            .unwrap_or_else(|| panic!("app MDD facade should find {resource_key}"));
        assert_eq!(app_resource.key, r"\cceu.css", "{resource_key}");
        assert_eq!(app_resource.mime_type, "text/css", "{resource_key}");
        assert_eq!(app_resource.data, css.data, "{resource_key}");
    }

    for key in resource_keys.iter().filter(|key| {
        key.ends_with(".png")
            || key.ends_with(".jpg")
            || key.ends_with(".jpeg")
            || key.ends_with(".gif")
            || key.ends_with(".mp3")
            || key.ends_with(".wav")
            || key.ends_with(".ogg")
    }) {
        let resource = mdd
            .locate_resource_result(key)
            .expect("real corpus media resource lookup should not fail")
            .expect("listed real corpus media resource should exist");
        assert!(!resource.data.is_empty());
        assert!(
            resource.mime_type.starts_with("image/") || resource.mime_type.starts_with("audio/"),
            "media key {key} should resolve to an image/audio MIME type, got {}",
            resource.mime_type
        );
    }
}

#[test]
fn native_mdx_lookup_inlines_real_corpus_mdd_from_env() {
    let Some(mdx_path) = real_corpus_path("RS_MDICT_TEST_MDX") else {
        return;
    };
    let Some(mdd_path) = real_corpus_path("RS_MDICT_TEST_MDD") else {
        return;
    };
    let query = std::env::var("RS_MDICT_TEST_QUERY")
        .ok()
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| "ability".to_string());
    let encryption_mode =
        detect_mdx_file_encryption_mode(&mdx_path).expect("real MDX header should be readable");

    let mut dictionary = mdx_dictionary(encryption_mode != MdxEncryptionMode::None, []);
    dictionary.service_id = "mdx::real-corpus".to_string();
    dictionary.display_name = "Real Corpus".to_string();
    dictionary.file_path = mdx_path;
    dictionary.mdd_file_paths = vec![mdd_path];
    let settings = mdx_settings_with_dictionary(dictionary);

    let result = run_native_mdx_lookup(
        &MdxLookupParams {
            dictionary_id: "mdx::real-corpus".to_string(),
            query: query.clone(),
            fuzzy: false,
        },
        &settings,
    )
    .expect("real MDX/MDD lookup should stay on the Rust-native route");

    assert_eq!(result.entries.len(), 1);
    assert_eq!(
        result.entries[0].key.to_ascii_lowercase(),
        query.to_ascii_lowercase()
    );
    assert!(result.mdd_resources_inlined);
    let html = &result.entries[0].html;
    assert!(html.contains("data:text/css;base64,"));
    assert!(!html.contains(r#"href="cceu.css""#));
    assert!(!html.contains(r#"href='cceu.css'"#));
}

#[test]
fn native_mdx_lookup_inlines_real_corpus_mdd_after_portable_copy_from_env() {
    let Some(source_mdx_path) = real_corpus_path("RS_MDICT_TEST_MDX") else {
        return;
    };
    let Some(source_mdd_path) = real_corpus_path("RS_MDICT_TEST_MDD") else {
        return;
    };
    let query = std::env::var("RS_MDICT_TEST_QUERY")
        .ok()
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| "ability".to_string());

    let portable_root = unique_temp_dir("easydict-real-corpus-portable-mdx");
    let dictionaries_dir = portable_root.join("dictionaries").join("collins");
    fs::create_dir_all(&dictionaries_dir).expect("portable dictionary directory should be created");
    let copied_mdx_path = dictionaries_dir.join("Collins COBUILD English Usage.mdx");
    let copied_mdd_path = dictionaries_dir.join("Collins COBUILD English Usage.mdd");
    fs::copy(&source_mdx_path, &copied_mdx_path).expect("real corpus MDX should copy");
    fs::copy(&source_mdd_path, &copied_mdd_path).expect("real corpus MDD should copy");

    let discovered_mdds = discover_mdd_file_paths(&path_string(&copied_mdx_path));
    assert_eq!(discovered_mdds, vec![path_string(&copied_mdd_path)]);

    let encryption_mode = detect_mdx_file_encryption_mode(&path_string(&copied_mdx_path))
        .expect("portable-copied real MDX header should be readable");
    let mut dictionary = mdx_dictionary(encryption_mode != MdxEncryptionMode::None, []);
    dictionary.service_id = "mdx::real-corpus-portable-copy".to_string();
    dictionary.display_name = "Real Corpus Portable Copy".to_string();
    dictionary.file_path = path_string(&copied_mdx_path);
    dictionary.mdd_file_paths = discovered_mdds;
    let settings = mdx_settings_with_dictionary(dictionary);

    let result = run_native_mdx_lookup(
        &MdxLookupParams {
            dictionary_id: "mdx::real-corpus-portable-copy".to_string(),
            query: query.clone(),
            fuzzy: false,
        },
        &settings,
    )
    .expect("portable-copied real MDX/MDD lookup should stay Rust-native");

    assert_eq!(result.entries.len(), 1);
    assert_eq!(
        result.entries[0].key.to_ascii_lowercase(),
        query.to_ascii_lowercase()
    );
    assert!(result.mdd_resources_inlined);
    let html = &result.entries[0].html;
    assert!(html.contains("data:text/css;base64,"));
    assert!(!html.contains(r#"href="cceu.css""#));
    assert!(!html.contains(r#"href='cceu.css'"#));
    for forbidden in ["CompatHost", ".NET", "Easydict.Workers"] {
        assert!(
            !html.contains(forbidden),
            "portable-copied real corpus route should not mention retained marker {forbidden}: {html}"
        );
    }

    fs::remove_dir_all(&portable_root).expect("portable real-corpus temp dir should be removed");
}

#[test]
fn native_mdx_lookup_real_corpus_does_not_inline_css_without_mdd_from_env() {
    let Some(mdx_path) = real_corpus_path("RS_MDICT_TEST_MDX") else {
        return;
    };
    let query = std::env::var("RS_MDICT_TEST_QUERY")
        .ok()
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| "ability".to_string());
    let encryption_mode =
        detect_mdx_file_encryption_mode(&mdx_path).expect("real MDX header should be readable");

    let mut dictionary = mdx_dictionary(encryption_mode != MdxEncryptionMode::None, []);
    dictionary.service_id = "mdx::real-corpus-no-mdd".to_string();
    dictionary.display_name = "Real Corpus Without MDD".to_string();
    dictionary.file_path = mdx_path;
    dictionary.mdd_file_paths.clear();
    let settings = mdx_settings_with_dictionary(dictionary);

    let result = run_native_mdx_lookup(
        &MdxLookupParams {
            dictionary_id: "mdx::real-corpus-no-mdd".to_string(),
            query: query.clone(),
            fuzzy: false,
        },
        &settings,
    )
    .expect("real corpus MDX lookup without MDD should still stay Rust-native");

    assert_eq!(result.entries.len(), 1);
    assert_eq!(
        result.entries[0].key.to_ascii_lowercase(),
        query.to_ascii_lowercase()
    );
    assert!(
        !result.mdd_resources_inlined,
        "MDD inline flag must prove a real companion resource was attached"
    );
    let html = &result.entries[0].html;
    assert!(!html.contains("data:text/css;base64,"));
    assert!(
        html.contains(r#"href="cceu.css""#) || html.contains(r#"href='cceu.css'"#),
        "without companion MDD, the original MDX stylesheet reference should remain"
    );
}

#[test]
fn native_mdx_lookup_inlines_first_hit_from_real_multi_mdd_fixtures() {
    let temp_dir = unique_temp_dir("easydict-native-mdd-real-first-hit-inline");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let first_mdd_path = temp_dir.join("Demo.mdd");
    let second_mdd_path = temp_dir.join("Demo.1.mdd");
    write_minimal_mdd_fixture(&first_mdd_path, &[(r"\images\logo.png", b"FIRST")]);
    write_minimal_mdd_fixture(&second_mdd_path, &[(r"\images\logo.png", b"SECOND")]);

    let mut dictionary = mdx_dictionary(false, []);
    dictionary.mdd_file_paths = vec![path_string(&first_mdd_path), path_string(&second_mdd_path)];
    let settings = mdx_settings_with_dictionary(dictionary);
    let mut mdx_factory = RecordingMdxReaderFactory::with_readers([RecordingMdxReader::new(
        [("apple", ("apple", r#"<img src="images/logo.png">"#))],
        [],
    )]);
    let mut mdd_factory = RsMdictMddReaderFactory;

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
    .expect("MDX lookup should inline the first real MDD resource hit");

    let html = &result.entries[0].html;
    assert!(html.contains(r#"src="data:image/png;base64,RklSU1Q=""#));
    assert!(!html.contains("U0VDT05E"));
    assert!(result.mdd_resources_inlined);

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
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
                    <a href="sound://audio/click.mp3">play</a>
                    <span style="background-image:url(images/bg.webp)"></span>
                    <a href="https://example.com/keep">external</a>
                    <a href="entry://banana">entry</a>
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
            (r"\audio\click.mp3", b"CLICK".as_slice()),
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
    assert!(html.contains(r#"href="data:audio/mpeg;base64,Q0xJQ0s=""#));
    assert!(html.contains("url('data:image/webp;base64,UklGRg==')"));
    assert!(html.contains(r#"href="https://example.com/keep""#));
    assert!(html.contains(r#"href="entry://banana""#));
    assert!(html.contains(r#"src="data:image/png;base64,OLD""#));
    assert!(html.contains(r#"href="javascript:alert(1)""#));
    assert!(!html.contains("https://dictassets/audio/pron.mp3"));
    assert!(!html.contains("sound://audio/click.mp3"));
    assert!(result.mdd_resources_inlined);
}

#[test]
fn native_mdx_lookup_can_skip_mdd_resource_inline_for_key_only_callers() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\demo.mdd"]);
    let settings = mdx_settings_with_dictionary(dictionary);
    let mut mdx_factory = RecordingMdxReaderFactory::with_readers([RecordingMdxReader::new(
        [("apple", ("apple", r#"<img src="images/logo.png">"#))],
        [],
    )]);
    let mut mdd_factory =
        RecordingMddReaderFactory::with_readers([Ok(RecordingMddReader::new([(
            r"\images\logo.png",
            b"\x89PNG".as_slice(),
        )]))]);

    let result = run_native_mdx_lookup_with_factories_and_mdd_policy(
        &mut mdx_factory,
        &mut mdd_factory,
        &MdxLookupParams {
            dictionary_id: "mdx::demo".to_string(),
            query: "apple".to_string(),
            fuzzy: false,
        },
        &settings,
        false,
    )
    .expect("MDX lookup should allow callers to skip MDD resource inlining");

    assert_eq!(mdd_factory.opened, Vec::<String>::new());
    assert_eq!(result.entries[0].html, r#"<img src="images/logo.png">"#);
    assert!(!result.mdd_resources_inlined);
}

#[test]
fn native_mdx_lookup_reports_no_mdd_inline_when_all_mdd_files_fail_to_open() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\bad.mdd", r"C:\Dicts\missing.mdd"]);
    let settings = mdx_settings_with_dictionary(dictionary);
    let mut mdx_factory = RecordingMdxReaderFactory::with_readers([RecordingMdxReader::new(
        [("apple", ("apple", r#"<img src="images/logo.png">"#))],
        [],
    )]);
    let mut mdd_factory = RecordingMddReaderFactory::with_readers([
        Err(NativeMddResourceError::new("bad MDD")),
        Err(NativeMddResourceError::new("missing MDD")),
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
    .expect("MDD open failures should not fail the MDX lookup");

    assert_eq!(
        mdd_factory.opened,
        [r"C:\Dicts\bad.mdd", r"C:\Dicts\missing.mdd"]
    );
    assert_eq!(result.entries[0].html, r#"<img src="images/logo.png">"#);
    assert!(!result.mdd_resources_inlined);
}

#[test]
fn native_mdx_lookup_continues_mdd_inline_after_lookup_error() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\bad.mdd", r"C:\Dicts\demo.mdd"]);
    let settings = mdx_settings_with_dictionary(dictionary);
    let mut mdx_factory = RecordingMdxReaderFactory::with_readers([RecordingMdxReader::new(
        [("apple", ("apple", r#"<img src="images/logo.png">"#))],
        [],
    )]);
    let mut mdd_factory = RecordingMddReaderFactory::with_readers([
        Ok(RecordingMddReader::failing_lookup("bad record block")),
        Ok(RecordingMddReader::new([(
            r"\images\logo.png",
            b"\x89PNG".as_slice(),
        )])),
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
    .expect("MDD lookup errors should not fail the MDX lookup");

    assert!(result.entries[0]
        .html
        .contains(r#"src="data:image/png;base64,iVBORw==""#));
    assert!(result.mdd_resources_inlined);
}

#[test]
fn native_mdd_html_inline_decodes_dictassets_urls_and_strips_cache_busters() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\demo.mdd"]);
    let mut mdd_factory = RecordingMddReaderFactory::with_readers([Ok(RecordingMddReader::new([
        (r"\images\logo large.png", b"\x89PNG".as_slice()),
        (r"\audio\hello world.mp3", b"ID3".as_slice()),
        (r"\styles\theme.css", b"body{}".as_slice()),
    ]))]);

    let html = inline_mdd_resources_in_html_with_factory(
        &mut mdd_factory,
        &dictionary,
        r#"<div>
            <img src="https://dictassets/images/logo%20large.png?v=1#hero">
            <audio src="audio/hello%20world.mp3?cache=skip"></audio>
            <span style="background:url('/styles/theme.css#dark')"></span>
        </div>"#,
    )
    .expect("MDD resource references should be rewritten");

    assert_eq!(mdd_factory.opened, [r"C:\Dicts\demo.mdd"]);
    assert!(html.contains(r#"src="data:image/png;base64,iVBORw==""#));
    assert!(html.contains(r#"src="data:audio/mpeg;base64,SUQz""#));
    assert!(html.contains("url('data:text/css;base64,Ym9keXt9')"));
    assert!(!html.contains("dictassets"));
    assert!(!html.contains("%20"));
}

#[test]
fn native_mdd_html_inline_decodes_utf8_percent_encoded_resource_paths() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\demo.mdd"]);
    let mut mdd_factory =
        RecordingMddReaderFactory::with_readers([Ok(RecordingMddReader::new([(
            r"\images\标志.png",
            b"\x89PNG".as_slice(),
        )]))]);

    let html = inline_mdd_resources_in_html_with_factory(
        &mut mdd_factory,
        &dictionary,
        r#"<div><img src="images/%E6%A0%87%E5%BF%97.png?v=1"></div>"#,
    )
    .expect("UTF-8 percent-encoded MDD resource references should be rewritten");

    assert_eq!(mdd_factory.opened, [r"C:\Dicts\demo.mdd"]);
    assert!(html.contains(r#"src="data:image/png;base64,iVBORw==""#));
    assert!(!html.contains("%E6%A0%87%E5%BF%97"));
}

#[test]
fn native_mdd_html_inline_decodes_html_entities_in_resource_paths() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\demo.mdd"]);
    let mut mdd_factory = RecordingMddReaderFactory::with_readers([Ok(RecordingMddReader::new([
        (r"\images\logo & mark.png", b"\x89PNG".as_slice()),
        (r"\images\quoted.png", b"QUOTE".as_slice()),
        (r"\audio\発音.mp3", b"ID3".as_slice()),
    ]))]);

    let html = inline_mdd_resources_in_html_with_factory(
        &mut mdd_factory,
        &dictionary,
        r#"<div>
            <img src="images/logo%20&amp;%20mark.png?cache=1&amp;theme=dark">
            <source srcset="images/&#x71;uoted.png 1x, https://example.com/keep.png 2x">
            <audio src="audio/&#30330;&#38899;.mp3"></audio>
            <span style="background:url(&quot;images/quoted.png&quot;)"></span>
        </div>"#,
    )
    .expect("HTML entity escaped MDD resource references should be rewritten");

    assert_eq!(mdd_factory.opened, [r"C:\Dicts\demo.mdd"]);
    assert!(html.contains(r#"src="data:image/png;base64,iVBORw==""#));
    assert!(html.contains(
        r#"srcset="data:image/png;base64,UVVPVEU= 1x, https://example.com/keep.png 2x""#
    ));
    assert!(html.contains(r#"src="data:audio/mpeg;base64,SUQz""#));
    assert!(html.contains("url('data:image/png;base64,UVVPVEU=')"));
    assert!(!html.contains("&amp;"));
    assert!(!html.contains("&#x71;"));
    assert!(!html.contains("&quot;"));
}

#[test]
fn native_mdd_html_inline_rewrites_srcset_poster_and_lazy_resource_attrs() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\demo.mdd"]);
    let mut mdd_factory = RecordingMddReaderFactory::with_readers([Ok(RecordingMddReader::new([
        (r"\images\hero small.png", b"SMALL".as_slice()),
        (r"\images\hero.png", b"HERO".as_slice()),
        (r"\images\logo.png", b"\x89PNG".as_slice()),
        (r"\images\poster.jpg", b"JPG".as_slice()),
        (r"\images\lazy.webp", b"RIFF".as_slice()),
        (r"\images\original.gif", b"GIF".as_slice()),
    ]))]);

    let html = inline_mdd_resources_in_html_with_factory(
        &mut mdd_factory,
        &dictionary,
        r#"<picture>
            <source srcset="images/hero%20small.png?v=1 1x, https://example.com/external.png 2x, data:image/png;base64,OLD 3x">
            <img srcset='images/hero.png 480w, /images/logo.png#main 960w'
                 poster=images/poster.jpg
                 data-src="images/lazy.webp"
                 data-original=images/original.gif>
        </picture>"#,
    )
    .expect("srcset and lazy MDD resource references should be rewritten");

    assert!(html.contains(
        r#"srcset="data:image/png;base64,U01BTEw= 1x, https://example.com/external.png 2x, data:image/png;base64,OLD 3x""#
    ));
    assert!(html.contains(
        "srcset='data:image/png;base64,SEVSTw== 480w, data:image/png;base64,iVBORw== 960w'"
    ));
    assert!(html.contains(r#"poster="data:image/jpeg;base64,SlBH""#));
    assert!(html.contains(r#"data-src="data:image/webp;base64,UklGRg==""#));
    assert!(html.contains(r#"data-original="data:image/gif;base64,R0lG""#));
    assert!(!html.contains("hero%20small.png"));
    assert!(!html.contains("poster=images/poster.jpg"));
}

#[test]
fn native_mdd_html_inline_rewrites_background_data_srcset_and_sound_variants() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\demo.mdd"]);
    let mut mdd_factory = RecordingMddReaderFactory::with_readers([Ok(RecordingMddReader::new([
        (r"\images\background.png", b"BG".as_slice()),
        (r"\images\lazy-two.webp", b"LAZY".as_slice()),
        (r"\images\original-two.gif", b"ORIG".as_slice()),
        (r"\images\source-two.png", b"SRCSET".as_slice()),
        (r"\audio\short.wav", b"WAV".as_slice()),
        (r"\audio\loose.ogg", b"OGG".as_slice()),
    ]))]);

    let html = inline_mdd_resources_in_html_with_factory(
        &mut mdd_factory,
        &dictionary,
        r#"<div background=images/background.png
                 data-lazy-src='images/lazy-two.webp'
                 data-original-src=images/original-two.gif>
            <source data-srcset="images/source-two.png 1x, https://example.com/keep.png 2x">
            <a href="sound:/audio/short.wav">short</a>
            <a href='sound:audio/loose.ogg?cache=1'>loose</a>
        </div>"#,
    )
    .expect("extra MDD resource reference variants should be rewritten");

    assert!(html.contains(r#"background="data:image/png;base64,Qkc=""#));
    assert!(html.contains(r#"data-lazy-src='data:image/webp;base64,TEFaWQ=='"#));
    assert!(html.contains(r#"data-original-src="data:image/gif;base64,T1JJRw==""#));
    assert!(html.contains(
        r#"data-srcset="data:image/png;base64,U1JDU0VU 1x, https://example.com/keep.png 2x""#
    ));
    assert!(html.contains(r#"href="data:audio/wav;base64,V0FW""#));
    assert!(html.contains(r#"href='data:audio/ogg;base64,T0dH'"#));
    assert!(!html.contains("sound:/audio/short.wav"));
    assert!(!html.contains("sound:audio/loose.ogg"));
}

#[test]
fn native_mdd_html_inline_normalizes_dot_relative_resource_paths() {
    let dictionary = mdx_dictionary(false, [r"C:\Dicts\demo.mdd"]);
    let mut mdd_factory = RecordingMddReaderFactory::with_readers([Ok(RecordingMddReader::new([
        (r"\images\logo.png", b"LOGO".as_slice()),
        (r"\images\hero.png", b"HERO".as_slice()),
        (r"\images\bg.webp", b"BG".as_slice()),
        (r"\audio\pron.mp3", b"ID3".as_slice()),
    ]))]);

    let html = inline_mdd_resources_in_html_with_factory(
        &mut mdd_factory,
        &dictionary,
        r#"<div>
            <img src="./images/logo.png">
            <source srcset="./images/hero.png 1x, .\images\logo.png 2x">
            <audio src='.\audio\pron.mp3'></audio>
            <span style="background:url(./images/bg.webp)"></span>
        </div>"#,
    )
    .expect("dot-relative MDD resource references should be rewritten");

    assert!(html.contains(r#"src="data:image/png;base64,TE9HTw==""#));
    assert!(html.contains(
        r#"srcset="data:image/png;base64,SEVSTw== 1x, data:image/png;base64,TE9HTw== 2x""#
    ));
    assert!(html.contains("src='data:audio/mpeg;base64,SUQz'"));
    assert!(html.contains("url('data:image/webp;base64,Qkc=')"));
    assert!(!html.contains("./images/"));
    assert!(!html.contains(r".\audio\pron.mp3"));
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
    assert_eq!(
        normalize_mdd_resource_key("./styles/dict.css").unwrap(),
        r"\styles\dict.css"
    );
    assert_eq!(
        normalize_mdd_resource_key(r".\styles\dict.css?cache=1#top").unwrap(),
        r"\styles\dict.css"
    );
    assert_eq!(
        normalize_mdd_resource_key("images%2Flogo.png").unwrap(),
        r"\images\logo.png"
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

#[test]
fn native_mdd_companion_discovery_matches_mdict_resource_file_convention() {
    let temp_dir = unique_temp_dir("easydict-native-mdd-discovery");
    fs::create_dir_all(&temp_dir).expect("temp dir should be created");
    let mdx_path = temp_dir.join("Oxford.mdx");
    let base_mdd = temp_dir.join("Oxford.mdd");
    let first_mdd = temp_dir.join("Oxford.1.mdd");
    let second_mdd = temp_dir.join("Oxford.2.mdd");
    let gap_mdd = temp_dir.join("oxford.4.MDD");
    let zero_mdd = temp_dir.join("Oxford.0.mdd");
    let non_numeric_mdd = temp_dir.join("Oxford.assets.mdd");

    fs::write(&mdx_path, b"mdx").expect("MDX file should be created");
    fs::write(&base_mdd, b"mdd").expect("base MDD file should be created");
    fs::write(&first_mdd, b"mdd1").expect("numbered MDD file should be created");
    fs::write(&second_mdd, b"mdd2").expect("numbered MDD file should be created");
    fs::write(&gap_mdd, b"mdd4").expect("gap MDD file should be created");
    fs::write(&zero_mdd, b"mdd0").expect("zero-suffix MDD file should be created");
    fs::write(&non_numeric_mdd, b"assets").expect("non-numeric MDD file should be created");

    assert_eq!(
        discover_mdd_file_paths(&path_string(&mdx_path)),
        vec![
            path_string(&base_mdd),
            path_string(&first_mdd),
            path_string(&second_mdd),
            path_string(&gap_mdd),
        ]
    );

    fs::remove_dir_all(&temp_dir).expect("temp dir should be removed");
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

fn real_corpus_path(env_name: &str) -> Option<String> {
    match std::env::var(env_name) {
        Ok(path) if !path.trim().is_empty() => Some(path),
        _ => {
            eprintln!("Skipping real-corpus test; set {env_name} to a local MDX/MDD file path");
            None
        }
    }
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

fn write_minimal_mdd_fixture(path: &Path, resources: &[(&str, &[u8])]) {
    assert!(!resources.is_empty());

    let mut file = fs::File::create(path).expect("MDD fixture should be created");
    let header_text = r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" KeyCaseSensitive="No" StripKey="Yes" />"#;
    let header_bytes = utf16_le(header_text);
    write_u32_be_file(&mut file, header_bytes.len() as u32);
    file.write_all(&header_bytes)
        .expect("MDD header should be written");
    file.write_all(&0u32.to_be_bytes())
        .expect("MDD header checksum should be written");

    let mut key_block_payload = Vec::new();
    let mut record_payload = Vec::new();
    for (key, data) in resources {
        push_u64_be_vec(&mut key_block_payload, record_payload.len() as u64);
        key_block_payload.extend_from_slice(&utf16_le(key));
        key_block_payload.extend_from_slice(&[0, 0]);
        record_payload.extend_from_slice(data);
    }

    let key_block = mdd_none_block(&key_block_payload);
    let key_info_payload = mdd_key_info_payload(
        resources.first().expect("first resource").0,
        resources.last().expect("last resource").0,
        resources.len() as u64,
        key_block.len() as u64,
        key_block_payload.len() as u64,
    );
    let key_info = mdd_zlib_block(&key_info_payload);

    write_u64_be_file(&mut file, 1);
    write_u64_be_file(&mut file, resources.len() as u64);
    write_u64_be_file(&mut file, key_info_payload.len() as u64);
    write_u64_be_file(&mut file, key_info.len() as u64);
    write_u64_be_file(&mut file, key_block.len() as u64);
    file.write_all(&0u32.to_be_bytes())
        .expect("MDD key header checksum should be written");
    file.write_all(&key_info)
        .expect("MDD key info should be written");
    file.write_all(&key_block)
        .expect("MDD key block should be written");

    let record_block = mdd_none_block(&record_payload);
    write_u64_be_file(&mut file, 1);
    write_u64_be_file(&mut file, resources.len() as u64);
    write_u64_be_file(&mut file, 16);
    write_u64_be_file(&mut file, record_block.len() as u64);
    write_u64_be_file(&mut file, record_block.len() as u64);
    write_u64_be_file(&mut file, record_payload.len() as u64);
    file.write_all(&record_block)
        .expect("MDD record block should be written");
}

fn write_record_encrypted_mdd_fixture(
    path: &Path,
    regcode: &str,
    email: &str,
    resources: &[(&str, &[u8])],
) {
    assert!(!resources.is_empty());

    let mut file = fs::File::create(path).expect("encrypted MDD fixture should be created");
    let header_text = r#"<Dictionary GeneratedByEngineVersion="2.0" RequiredEngineVersion="2.0" Encoding="UTF-8" Encrypted="1" />"#;
    let header_bytes = utf16_le(header_text);
    write_u32_be_file(&mut file, header_bytes.len() as u32);
    file.write_all(&header_bytes)
        .expect("encrypted MDD header should be written");
    file.write_all(&0u32.to_be_bytes())
        .expect("encrypted MDD header checksum should be written");

    let mut key_block_payload = Vec::new();
    let mut record_payload = Vec::new();
    for (key, data) in resources {
        push_u64_be_vec(&mut key_block_payload, record_payload.len() as u64);
        key_block_payload.extend_from_slice(&utf16_le(key));
        key_block_payload.extend_from_slice(&[0, 0]);
        record_payload.extend_from_slice(data);
    }

    let key_block = mdd_none_block(&key_block_payload);
    let key_info_payload = mdd_key_info_payload(
        resources.first().expect("first resource").0,
        resources.last().expect("last resource").0,
        resources.len() as u64,
        key_block.len() as u64,
        key_block_payload.len() as u64,
    );
    let key_info = mdd_zlib_block(&key_info_payload);

    let mut key_header = Vec::new();
    push_u64_be_vec(&mut key_header, 1);
    push_u64_be_vec(&mut key_header, resources.len() as u64);
    push_u64_be_vec(&mut key_header, key_info_payload.len() as u64);
    push_u64_be_vec(&mut key_header, key_info.len() as u64);
    push_u64_be_vec(&mut key_header, key_block.len() as u64);

    let regcode = mdx_decode_base64_regcode(regcode).expect("test regcode should be valid");
    let key = mdx_decrypt_regcode_by_email(&regcode, email)
        .expect("test email regcode should derive key header key");
    let encrypted_key_header =
        mdx_salsa20_8(&key_header, &key).expect("MDD key header should encrypt");
    file.write_all(&encrypted_key_header)
        .expect("encrypted MDD key header should be written");
    file.write_all(&0u32.to_be_bytes())
        .expect("encrypted MDD key header checksum should be written");

    file.write_all(&key_info)
        .expect("encrypted MDD key info should be written");
    file.write_all(&key_block)
        .expect("encrypted MDD key block should be written");

    let record_block = mdx_encrypt_block(&mdd_none_block(&record_payload));
    write_u64_be_file(&mut file, 1);
    write_u64_be_file(&mut file, resources.len() as u64);
    write_u64_be_file(&mut file, 16);
    write_u64_be_file(&mut file, record_block.len() as u64);
    write_u64_be_file(&mut file, record_block.len() as u64);
    write_u64_be_file(&mut file, record_payload.len() as u64);
    file.write_all(&record_block)
        .expect("encrypted MDD record block should be written");
}

fn mdd_key_info_payload(
    first_key: &str,
    last_key: &str,
    resource_count: u64,
    key_block_pack_size: u64,
    key_block_unpack_size: u64,
) -> Vec<u8> {
    let mut payload = Vec::new();
    push_u64_be_vec(&mut payload, resource_count);
    push_u16_be_vec(&mut payload, first_key.encode_utf16().count() as u16);
    payload.extend_from_slice(&utf16_le(first_key));
    payload.extend_from_slice(&[0, 0]);
    push_u16_be_vec(&mut payload, last_key.encode_utf16().count() as u16);
    payload.extend_from_slice(&utf16_le(last_key));
    payload.extend_from_slice(&[0, 0]);
    push_u64_be_vec(&mut payload, key_block_pack_size);
    push_u64_be_vec(&mut payload, key_block_unpack_size);
    payload
}

fn mdd_none_block(payload: &[u8]) -> Vec<u8> {
    let mut block = vec![0, 0, 0, 0, 0, 0, 0, 0];
    block.extend_from_slice(payload);
    block
}

fn mdd_zlib_block(payload: &[u8]) -> Vec<u8> {
    let mut encoder = ZlibEncoder::new(Vec::new(), Compression::default());
    encoder
        .write_all(payload)
        .expect("MDD key info should compress");
    let compressed = encoder.finish().expect("MDD key info compression");
    let mut block = vec![2, 0, 0, 0, 0, 0, 0, 0];
    block.extend_from_slice(&compressed);
    block
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

fn push_u16_be_vec(bytes: &mut Vec<u8>, value: u16) {
    bytes.extend_from_slice(&value.to_be_bytes());
}

fn push_u64_be_vec(bytes: &mut Vec<u8>, value: u64) {
    bytes.extend_from_slice(&value.to_be_bytes());
}

fn write_u32_be_file(file: &mut fs::File, value: u32) {
    file.write_all(&value.to_be_bytes())
        .expect("u32 should be written");
}

fn write_u64_be_file(file: &mut fs::File, value: u64) {
    file.write_all(&value.to_be_bytes())
        .expect("u64 should be written");
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

    fn open_mdd(
        &mut self,
        _dictionary: &ImportedMdxDictionarySnapshot,
        path: &str,
    ) -> Result<Self::Reader, NativeMddResourceError> {
        self.opened.push(path.to_string());
        self.readers
            .pop_front()
            .ok_or_else(|| NativeMddResourceError::new("test MDD reader was not queued"))?
    }
}

struct RecordingMddReader {
    resources: HashMap<String, Vec<u8>>,
    lookup_error: Option<NativeMddResourceError>,
}

impl RecordingMddReader {
    fn new(resources: impl IntoIterator<Item = (&'static str, &'static [u8])>) -> Self {
        Self {
            resources: resources
                .into_iter()
                .map(|(key, data)| (key.to_string(), data.to_vec()))
                .collect(),
            lookup_error: None,
        }
    }

    fn failing_lookup(message: &'static str) -> Self {
        Self {
            resources: HashMap::new(),
            lookup_error: Some(NativeMddResourceError::new(message)),
        }
    }
}

impl NativeMddResourceReader for RecordingMddReader {
    fn locate_raw(
        &mut self,
        resource_key: &str,
    ) -> Result<Option<(String, Vec<u8>)>, NativeMddResourceError> {
        if let Some(error) = &self.lookup_error {
            return Err(error.clone());
        }

        Ok(self
            .resources
            .get(resource_key)
            .cloned()
            .map(|data| (resource_key.to_string(), data)))
    }
}
