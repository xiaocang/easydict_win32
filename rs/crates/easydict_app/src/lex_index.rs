use std::collections::{BTreeMap, BTreeSet, HashSet};
use std::fmt;
use std::fs;
use std::path::Path;
use unicode_normalization::UnicodeNormalization;

const FORMAT_VERSION: i32 = 1;
const NORMALIZATION_KIND: i32 = 1;
const NORMALIZATION_ID: &str = "nfkc-lower-invariant-v1";
const MAGIC: &[u8; 4] = b"LXDX";
const HEADER_SIZE: usize = 4 + 9 * 4;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LexIndexMetadata {
    pub format_version: i32,
    pub normalization_id: String,
    pub state_count: usize,
    pub edge_count: usize,
    pub entry_count: usize,
    pub payload_count: usize,
    pub value_ref_count: usize,
    pub string_count: usize,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LexIndexError {
    message: String,
}

impl LexIndexError {
    fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }
}

impl fmt::Display for LexIndexError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for LexIndexError {}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LexIndex {
    metadata: LexIndexMetadata,
    states: Vec<StateRecord>,
    edges: Vec<EdgeRecord>,
    payloads: Vec<PayloadRecord>,
    value_refs: Vec<usize>,
    string_offsets: Vec<usize>,
    string_bytes: Vec<u8>,
}

impl LexIndex {
    pub fn from_keys(keys: impl IntoIterator<Item = impl AsRef<str>>) -> Self {
        let groups = collect_grouped_keys(keys);
        build_from_groups(groups)
    }

    pub fn build_bytes(keys: impl IntoIterator<Item = impl AsRef<str>>) -> Vec<u8> {
        Self::from_keys(keys).to_bytes()
    }

    pub fn open(path: impl AsRef<Path>) -> Result<Self, LexIndexError> {
        let bytes = fs::read(path).map_err(|error| {
            LexIndexError::new(format!("Could not read LexIndex file: {error}"))
        })?;
        Self::open_bytes(&bytes)
    }

    pub fn open_bytes(bytes: &[u8]) -> Result<Self, LexIndexError> {
        if bytes.len() < HEADER_SIZE || &bytes[..4] != MAGIC {
            return Err(LexIndexError::new("Invalid LexIndex file header."));
        }

        let version = read_i32(bytes, 4)?;
        if version != FORMAT_VERSION {
            return Err(LexIndexError::new(format!(
                "Unsupported LexIndex format version {version}."
            )));
        }

        let normalization_kind = read_i32(bytes, 8)?;
        if normalization_kind != NORMALIZATION_KIND {
            return Err(LexIndexError::new(format!(
                "Unsupported normalization kind {normalization_kind}."
            )));
        }

        let state_count = read_count(bytes, 12, "state count")?;
        let edge_count = read_count(bytes, 16, "edge count")?;
        let entry_count = read_count(bytes, 20, "entry count")?;
        let payload_count = read_count(bytes, 24, "payload count")?;
        let value_ref_count = read_count(bytes, 28, "value reference count")?;
        let string_count = read_count(bytes, 32, "string count")?;
        let string_byte_count = read_count(bytes, 36, "string byte count")?;

        if state_count == 0 {
            return Err(LexIndexError::new("LexIndex file contains invalid counts."));
        }

        let mut offset = HEADER_SIZE;
        let states = read_records(bytes, &mut offset, state_count, read_state_record)?;
        let edges = read_records(bytes, &mut offset, edge_count, read_edge_record)?;
        let payloads = read_records(bytes, &mut offset, payload_count, read_payload_record)?;
        let value_refs = read_usize_array(bytes, &mut offset, value_ref_count)?;
        let string_offsets = read_usize_array(bytes, &mut offset, string_count + 1)?;
        let string_end = offset
            .checked_add(string_byte_count)
            .ok_or_else(|| LexIndexError::new("LexIndex string pool is too large."))?;
        if string_end > bytes.len() {
            return Err(LexIndexError::new(
                "Unexpected end of LexIndex string pool.",
            ));
        }
        let string_bytes = bytes[offset..string_end].to_vec();

        validate_index(
            &states,
            &edges,
            &payloads,
            &value_refs,
            &string_offsets,
            string_bytes.len(),
        )?;

        Ok(Self {
            metadata: LexIndexMetadata {
                format_version: version,
                normalization_id: NORMALIZATION_ID.to_string(),
                state_count,
                edge_count,
                entry_count,
                payload_count,
                value_ref_count,
                string_count,
            },
            states,
            edges,
            payloads,
            value_refs,
            string_offsets,
            string_bytes,
        })
    }

    pub fn metadata(&self) -> &LexIndexMetadata {
        &self.metadata
    }

    pub fn complete(&self, prefix: &str, limit: usize) -> Vec<String> {
        if limit == 0 {
            return Vec::new();
        }

        let normalized = normalize_key(prefix);
        if normalized.is_empty() {
            return Vec::new();
        }

        let Some(state_id) = self.traverse_exact(&normalized) else {
            return Vec::new();
        };

        let mut results = Vec::new();
        self.collect_completions(state_id, &mut results, limit);
        results
    }

    pub fn match_pattern(&self, pattern: &str, limit: usize) -> Vec<String> {
        if limit == 0 {
            return Vec::new();
        }

        let normalized = normalize_key(pattern);
        if normalized.is_empty() {
            return Vec::new();
        }

        let pattern = normalized.chars().collect::<Vec<_>>();
        let mut results = Vec::new();
        let mut seen = HashSet::new();
        let mut dead_ends = HashSet::new();
        self.match_core(
            0,
            &pattern,
            0,
            &mut results,
            &mut seen,
            &mut dead_ends,
            limit,
        );
        results.sort_by(|left, right| {
            normalize_key(left)
                .cmp(&normalize_key(right))
                .then_with(|| left.cmp(right))
        });
        results
    }

    pub fn to_bytes(&self) -> Vec<u8> {
        let mut bytes = Vec::new();
        bytes.extend_from_slice(MAGIC);
        push_i32(&mut bytes, FORMAT_VERSION);
        push_i32(&mut bytes, NORMALIZATION_KIND);
        push_i32(&mut bytes, self.states.len() as i32);
        push_i32(&mut bytes, self.edges.len() as i32);
        push_i32(&mut bytes, self.metadata.entry_count as i32);
        push_i32(&mut bytes, self.payloads.len() as i32);
        push_i32(&mut bytes, self.value_refs.len() as i32);
        push_i32(&mut bytes, self.metadata.string_count as i32);
        push_i32(&mut bytes, self.string_bytes.len() as i32);

        for state in &self.states {
            push_i32(&mut bytes, state.first_edge_index as i32);
            push_i32(&mut bytes, state.edge_count as i32);
            push_i32(&mut bytes, state.payload_index);
        }

        for edge in &self.edges {
            push_i32(&mut bytes, edge.label as i32);
            push_i32(&mut bytes, edge.target_state_id as i32);
        }

        for payload in &self.payloads {
            push_i32(&mut bytes, payload.first_value_ref_index as i32);
            push_i32(&mut bytes, payload.value_count as i32);
        }

        for value_ref in &self.value_refs {
            push_i32(&mut bytes, *value_ref as i32);
        }

        for offset in &self.string_offsets {
            push_i32(&mut bytes, *offset as i32);
        }

        bytes.extend_from_slice(&self.string_bytes);
        bytes
    }

    fn traverse_exact(&self, value: &str) -> Option<usize> {
        let mut state_id = 0;
        for ch in value.chars() {
            state_id = self.find_transition(state_id, ch as u32)?;
        }
        Some(state_id)
    }

    fn collect_completions(&self, state_id: usize, results: &mut Vec<String>, limit: usize) {
        if results.len() >= limit {
            return;
        }

        let state = self.states[state_id];
        if state.payload_index >= 0 {
            self.add_payload_values(state.payload_index as usize, results, limit);
            if results.len() >= limit {
                return;
            }
        }

        for edge in &self.edges[state.first_edge_index..state.first_edge_index + state.edge_count] {
            self.collect_completions(edge.target_state_id, results, limit);
            if results.len() >= limit {
                return;
            }
        }
    }

    fn match_core(
        &self,
        state_id: usize,
        pattern: &[char],
        pattern_position: usize,
        results: &mut Vec<String>,
        seen: &mut HashSet<String>,
        dead_ends: &mut HashSet<(usize, usize)>,
        limit: usize,
    ) -> bool {
        if results.len() >= limit || dead_ends.contains(&(state_id, pattern_position)) {
            return false;
        }

        let mut found_any = false;
        let state = self.states[state_id];

        if pattern_position == pattern.len() {
            if state.payload_index >= 0 {
                self.add_unique_payload_values(state.payload_index as usize, results, seen, limit);
                found_any = true;
            }

            if !found_any {
                dead_ends.insert((state_id, pattern_position));
            }
            return found_any;
        }

        match pattern[pattern_position] {
            '*' => {
                found_any |= self.match_core(
                    state_id,
                    pattern,
                    pattern_position + 1,
                    results,
                    seen,
                    dead_ends,
                    limit,
                );
                for edge in
                    &self.edges[state.first_edge_index..state.first_edge_index + state.edge_count]
                {
                    if results.len() >= limit {
                        break;
                    }

                    found_any |= self.match_core(
                        edge.target_state_id,
                        pattern,
                        pattern_position,
                        results,
                        seen,
                        dead_ends,
                        limit,
                    );
                }
            }
            '?' => {
                for edge in
                    &self.edges[state.first_edge_index..state.first_edge_index + state.edge_count]
                {
                    if results.len() >= limit {
                        break;
                    }

                    found_any |= self.match_core(
                        edge.target_state_id,
                        pattern,
                        pattern_position + 1,
                        results,
                        seen,
                        dead_ends,
                        limit,
                    );
                }
            }
            ch => {
                if let Some(next_state_id) = self.find_transition(state_id, ch as u32) {
                    found_any = self.match_core(
                        next_state_id,
                        pattern,
                        pattern_position + 1,
                        results,
                        seen,
                        dead_ends,
                        limit,
                    );
                }
            }
        }

        if !found_any && results.len() < limit {
            dead_ends.insert((state_id, pattern_position));
        }

        found_any
    }

    fn find_transition(&self, state_id: usize, label: u32) -> Option<usize> {
        let state = self.states[state_id];
        let edges = &self.edges[state.first_edge_index..state.first_edge_index + state.edge_count];
        edges
            .binary_search_by_key(&label, |edge| edge.label)
            .ok()
            .map(|index| edges[index].target_state_id)
    }

    fn add_payload_values(&self, payload_index: usize, results: &mut Vec<String>, limit: usize) {
        let payload = self.payloads[payload_index];
        for value_ref in &self.value_refs
            [payload.first_value_ref_index..payload.first_value_ref_index + payload.value_count]
        {
            results.push(self.read_string(*value_ref));
            if results.len() >= limit {
                return;
            }
        }
    }

    fn add_unique_payload_values(
        &self,
        payload_index: usize,
        results: &mut Vec<String>,
        seen: &mut HashSet<String>,
        limit: usize,
    ) {
        let payload = self.payloads[payload_index];
        for value_ref in &self.value_refs
            [payload.first_value_ref_index..payload.first_value_ref_index + payload.value_count]
        {
            let value = self.read_string(*value_ref);
            if seen.insert(value.clone()) {
                results.push(value);
            }

            if results.len() >= limit {
                return;
            }
        }
    }

    fn read_string(&self, string_index: usize) -> String {
        let start = self.string_offsets[string_index];
        let end = self.string_offsets[string_index + 1];
        String::from_utf8_lossy(&self.string_bytes[start..end]).into_owned()
    }
}

pub fn normalize_key(value: &str) -> String {
    value
        .trim()
        .nfkc()
        .flat_map(char::to_lowercase)
        .collect::<String>()
}

fn collect_grouped_keys(
    keys: impl IntoIterator<Item = impl AsRef<str>>,
) -> BTreeMap<String, BTreeSet<String>> {
    let mut groups = BTreeMap::<String, BTreeSet<String>>::new();
    for key in keys {
        let trimmed = key.as_ref().trim();
        if trimmed.is_empty() {
            continue;
        }

        let normalized = normalize_key(trimmed);
        if normalized.is_empty() {
            continue;
        }

        groups
            .entry(normalized)
            .or_default()
            .insert(trimmed.to_string());
    }
    groups
}

fn build_from_groups(groups: BTreeMap<String, BTreeSet<String>>) -> LexIndex {
    let mut trie_nodes = vec![TrieNode::default()];
    let mut unique_strings = Vec::<String>::new();
    let mut string_to_index = BTreeMap::<String, usize>::new();
    let mut value_refs = Vec::<usize>::new();
    let mut payloads = Vec::<PayloadRecord>::new();

    for (payload_index, (normalized, originals)) in groups.iter().enumerate() {
        let mut state_id = 0;
        for ch in normalized.chars() {
            let label = ch as u32;
            let existing = trie_nodes[state_id].edges.get(&label).copied();
            state_id = match existing {
                Some(child_id) => child_id,
                None => {
                    let child_id = trie_nodes.len();
                    trie_nodes.push(TrieNode::default());
                    trie_nodes[state_id].edges.insert(label, child_id);
                    child_id
                }
            };
        }
        trie_nodes[state_id].payload_index = payload_index as i32;

        let first_value_ref_index = value_refs.len();
        for original in originals {
            let string_index = match string_to_index.get(original).copied() {
                Some(index) => index,
                None => {
                    let index = unique_strings.len();
                    unique_strings.push(original.clone());
                    string_to_index.insert(original.clone(), index);
                    index
                }
            };
            value_refs.push(string_index);
        }
        payloads.push(PayloadRecord {
            first_value_ref_index,
            value_count: originals.len(),
        });
    }

    let mut states = Vec::with_capacity(trie_nodes.len());
    let mut edges = Vec::<EdgeRecord>::new();
    for node in &trie_nodes {
        let first_edge_index = edges.len();
        for (label, target_state_id) in &node.edges {
            edges.push(EdgeRecord {
                label: *label,
                target_state_id: *target_state_id,
            });
        }
        states.push(StateRecord {
            first_edge_index,
            edge_count: node.edges.len(),
            payload_index: node.payload_index,
        });
    }

    let mut string_offsets = Vec::with_capacity(unique_strings.len() + 1);
    let mut string_bytes = Vec::new();
    for value in &unique_strings {
        string_offsets.push(string_bytes.len());
        string_bytes.extend_from_slice(value.as_bytes());
    }
    string_offsets.push(string_bytes.len());

    LexIndex {
        metadata: LexIndexMetadata {
            format_version: FORMAT_VERSION,
            normalization_id: NORMALIZATION_ID.to_string(),
            state_count: states.len(),
            edge_count: edges.len(),
            entry_count: groups.len(),
            payload_count: payloads.len(),
            value_ref_count: value_refs.len(),
            string_count: unique_strings.len(),
        },
        states,
        edges,
        payloads,
        value_refs,
        string_offsets,
        string_bytes,
    }
}

fn read_i32(bytes: &[u8], offset: usize) -> Result<i32, LexIndexError> {
    let end = offset
        .checked_add(4)
        .ok_or_else(|| LexIndexError::new("LexIndex file is too large."))?;
    let chunk = bytes
        .get(offset..end)
        .ok_or_else(|| LexIndexError::new("Unexpected end of LexIndex file."))?;
    Ok(i32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]))
}

fn read_count(bytes: &[u8], offset: usize, label: &str) -> Result<usize, LexIndexError> {
    let value = read_i32(bytes, offset)?;
    if value < 0 {
        return Err(LexIndexError::new(format!(
            "LexIndex file contains invalid {label}."
        )));
    }
    Ok(value as usize)
}

fn read_records<T>(
    bytes: &[u8],
    offset: &mut usize,
    count: usize,
    reader: impl Fn(&[u8], &mut usize) -> Result<T, LexIndexError>,
) -> Result<Vec<T>, LexIndexError> {
    let mut records = Vec::with_capacity(count);
    for _ in 0..count {
        records.push(reader(bytes, offset)?);
    }
    Ok(records)
}

fn read_state_record(bytes: &[u8], offset: &mut usize) -> Result<StateRecord, LexIndexError> {
    Ok(StateRecord {
        first_edge_index: read_usize(bytes, offset)?,
        edge_count: read_usize(bytes, offset)?,
        payload_index: read_i32_at(bytes, offset)?,
    })
}

fn read_edge_record(bytes: &[u8], offset: &mut usize) -> Result<EdgeRecord, LexIndexError> {
    Ok(EdgeRecord {
        label: read_usize(bytes, offset)? as u32,
        target_state_id: read_usize(bytes, offset)?,
    })
}

fn read_payload_record(bytes: &[u8], offset: &mut usize) -> Result<PayloadRecord, LexIndexError> {
    Ok(PayloadRecord {
        first_value_ref_index: read_usize(bytes, offset)?,
        value_count: read_usize(bytes, offset)?,
    })
}

fn read_usize_array(
    bytes: &[u8],
    offset: &mut usize,
    count: usize,
) -> Result<Vec<usize>, LexIndexError> {
    let mut values = Vec::with_capacity(count);
    for _ in 0..count {
        values.push(read_usize(bytes, offset)?);
    }
    Ok(values)
}

fn read_usize(bytes: &[u8], offset: &mut usize) -> Result<usize, LexIndexError> {
    let value = read_i32_at(bytes, offset)?;
    if value < 0 {
        return Err(LexIndexError::new(
            "LexIndex file contains invalid negative value.",
        ));
    }
    Ok(value as usize)
}

fn read_i32_at(bytes: &[u8], offset: &mut usize) -> Result<i32, LexIndexError> {
    let value = read_i32(bytes, *offset)?;
    *offset = (*offset)
        .checked_add(4)
        .ok_or_else(|| LexIndexError::new("LexIndex file is too large."))?;
    Ok(value)
}

fn validate_index(
    states: &[StateRecord],
    edges: &[EdgeRecord],
    payloads: &[PayloadRecord],
    value_refs: &[usize],
    string_offsets: &[usize],
    string_byte_count: usize,
) -> Result<(), LexIndexError> {
    for (index, state) in states.iter().enumerate() {
        let edge_end = state
            .first_edge_index
            .checked_add(state.edge_count)
            .ok_or_else(|| LexIndexError::new(format!("State {index} has invalid edge bounds.")))?;
        if edge_end > edges.len() {
            return Err(LexIndexError::new(format!(
                "State {index} has invalid edge bounds."
            )));
        }

        if state.payload_index >= payloads.len() as i32 {
            return Err(LexIndexError::new(format!(
                "State {index} points to invalid payload index."
            )));
        }

        let mut previous_label = None;
        for (edge_index, edge) in edges[state.first_edge_index..edge_end].iter().enumerate() {
            if edge.target_state_id >= states.len() {
                return Err(LexIndexError::new(format!(
                    "Edge {} points to invalid target state.",
                    state.first_edge_index + edge_index
                )));
            }

            if previous_label.is_some_and(|previous| edge.label < previous) {
                return Err(LexIndexError::new(format!(
                    "State {index} edges are not ordered."
                )));
            }
            previous_label = Some(edge.label);
        }
    }

    for (index, payload) in payloads.iter().enumerate() {
        let value_ref_end = payload
            .first_value_ref_index
            .checked_add(payload.value_count)
            .ok_or_else(|| {
                LexIndexError::new(format!(
                    "Payload {index} has invalid string reference bounds."
                ))
            })?;
        if value_ref_end > value_refs.len() {
            return Err(LexIndexError::new(format!(
                "Payload {index} has invalid string reference bounds."
            )));
        }
    }

    if string_offsets.is_empty()
        || string_offsets[0] != 0
        || string_offsets[string_offsets.len() - 1] != string_byte_count
    {
        return Err(LexIndexError::new("String pool offsets are invalid."));
    }

    for offsets in string_offsets.windows(2) {
        if offsets[1] < offsets[0] {
            return Err(LexIndexError::new("String pool offsets are not monotonic."));
        }
    }

    let string_count = string_offsets.len() - 1;
    for (index, value_ref) in value_refs.iter().enumerate() {
        if *value_ref >= string_count {
            return Err(LexIndexError::new(format!(
                "String reference {index} is out of range."
            )));
        }
    }

    Ok(())
}

fn push_i32(bytes: &mut Vec<u8>, value: i32) {
    bytes.extend_from_slice(&value.to_le_bytes());
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct StateRecord {
    first_edge_index: usize,
    edge_count: usize,
    payload_index: i32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct EdgeRecord {
    label: u32,
    target_state_id: usize,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct PayloadRecord {
    first_value_ref_index: usize,
    value_count: usize,
}

struct TrieNode {
    edges: BTreeMap<u32, usize>,
    payload_index: i32,
}

impl Default for TrieNode {
    fn default() -> Self {
        Self {
            edges: BTreeMap::new(),
            payload_index: -1,
        }
    }
}
