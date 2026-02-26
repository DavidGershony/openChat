//! Marmot client implementation using the real MDK library.

use std::sync::Arc;

use mdk_core::{MDK, MdkConfig};
use mdk_memory_storage::MdkMemoryStorage;
use nostr::{Event, EventId, Keys, PublicKey, RelayUrl, UnsignedEvent};
use parking_lot::RwLock;

use crate::error::MarmotError;

/// The main Marmot client that wraps MDK for FFI access.
pub struct MarmotClient {
    /// Nostr keys for this client
    keys: Keys,
    /// The MDK instance
    mdk: Arc<RwLock<MDK<MdkMemoryStorage>>>,
    /// Default relays for group operations
    default_relays: Vec<RelayUrl>,
}

impl MarmotClient {
    /// Create a new Marmot client with the given Nostr identity.
    pub fn new(private_key_hex: &str, _public_key_hex: &str) -> Result<Self, MarmotError> {
        // Parse the private key to get Keys
        let secret_key = nostr::SecretKey::from_hex(private_key_hex)
            .map_err(|e| MarmotError::InvalidKey(format!("Invalid private key: {}", e)))?;
        let keys = Keys::new(secret_key);

        // Create MDK with in-memory storage
        let storage = MdkMemoryStorage::new();
        let config = MdkConfig::default();
        let mdk = MDK::builder(storage)
            .with_config(config)
            .build();

        // Default relays
        let default_relays = vec![
            RelayUrl::parse("wss://relay.damus.io").unwrap(),
            RelayUrl::parse("wss://nos.lol").unwrap(),
        ];

        Ok(Self {
            keys,
            mdk: Arc::new(RwLock::new(mdk)),
            default_relays,
        })
    }

    /// Generate a new KeyPackage for group invitations.
    /// Returns JSON with { "content": "<base64>", "tags": [[...], ...] }
    pub fn generate_key_package(&self) -> Result<Vec<u8>, MarmotError> {
        let mdk = self.mdk.read();
        let public_key = self.keys.public_key();

        // Create key package for a Nostr event - MDK returns both content and required tags
        let (key_package_base64, mdk_tags) = mdk
            .create_key_package_for_event(&public_key, self.default_relays.clone())
            .map_err(|e| MarmotError::Internal(format!("Failed to create key package: {}", e)))?;

        // Convert nostr::Tag array to Vec<Vec<String>> for JSON serialization
        let tags: Vec<Vec<String>> = mdk_tags
            .into_iter()
            .map(|tag| tag.to_vec())
            .collect();

        // Return both content and tags as JSON
        #[derive(serde::Serialize)]
        struct KeyPackageResult {
            content: String,
            tags: Vec<Vec<String>>,
        }

        let result = KeyPackageResult {
            content: key_package_base64,
            tags,
        };

        serde_json::to_vec(&result)
            .map_err(|e| MarmotError::SerializationError(format!("Failed to serialize: {}", e)))
    }

    /// Create a new MLS group.
    /// Returns (group_id, epoch).
    pub fn create_group(&self, name: &str) -> Result<(Vec<u8>, u64), MarmotError> {
        let mdk = self.mdk.write();
        let public_key = self.keys.public_key();

        // Create group config
        let config = mdk_core::groups::NostrGroupConfigData {
            name: name.to_string(),
            description: String::new(),
            image_hash: None,
            image_key: None,
            image_nonce: None,
            relays: self.default_relays.clone(),
            admins: vec![public_key.clone()],
        };

        // Create the group (no initial members besides creator)
        let result = mdk
            .create_group(&public_key, vec![], config)
            .map_err(|e| MarmotError::Internal(format!("Failed to create group: {}", e)))?;

        // Get the group ID as bytes
        let group_id = result.group.mls_group_id.as_slice().to_vec();
        let epoch = 0u64; // New groups start at epoch 0

        Ok((group_id, epoch))
    }

    /// Add a member to a group using their KeyPackage event.
    /// key_package_event_json: JSON-serialized Nostr event containing the key package
    /// Returns JSON object with { "welcome": [...], "commit": {...} }
    pub fn add_member(&self, group_id: &[u8], key_package_event_json: &[u8]) -> Result<Vec<u8>, MarmotError> {
        let mdk = self.mdk.write();

        // Parse the group ID
        let mls_group_id = mdk_core::GroupId::from_slice(group_id);

        // Parse the key package event from JSON
        let event_json = std::str::from_utf8(key_package_event_json)
            .map_err(|e| MarmotError::Internal(format!("Invalid UTF-8 in event JSON: {}", e)))?;
        let event: Event = serde_json::from_str(event_json)
            .map_err(|e| MarmotError::Internal(format!("Invalid event JSON: {}", e)))?;

        // Add the member
        let result = mdk
            .add_members(&mls_group_id, &[event])
            .map_err(|e| MarmotError::Internal(format!("Failed to add member: {}", e)))?;

        // Merge the pending commit
        mdk.merge_pending_commit(&mls_group_id)
            .map_err(|e| MarmotError::Internal(format!("Failed to merge commit: {}", e)))?;

        // Build response with both welcome and commit data
        #[derive(serde::Serialize)]
        struct AddMemberResult {
            welcome: Option<serde_json::Value>,
            commit: Option<serde_json::Value>,
        }

        let response = AddMemberResult {
            welcome: result.welcome_rumors.map(|r| serde_json::to_value(r).ok()).flatten(),
            commit: Some(serde_json::to_value(&result.evolution_event).unwrap_or_default()),
        };

        serde_json::to_vec(&response)
            .map_err(|e| MarmotError::SerializationError(format!("Failed to serialize result: {}", e)))
    }

    /// Process a Welcome message to join a group.
    /// welcome_event_json: JSON containing wrapper_event_id and rumor_event
    /// Returns (group_id, group_name, epoch, members_json).
    pub fn process_welcome(&self, welcome_data: &[u8]) -> Result<(Vec<u8>, String, u64, Vec<String>), MarmotError> {
        let mdk = self.mdk.write();

        // Parse the welcome data (expecting a JSON object with event_id and rumor)
        #[derive(serde::Deserialize)]
        struct WelcomeInput {
            wrapper_event_id: String,
            rumor_event: serde_json::Value,
        }

        let welcome_json = std::str::from_utf8(welcome_data)
            .map_err(|e| MarmotError::Internal(format!("Invalid UTF-8: {}", e)))?;
        let input: WelcomeInput = serde_json::from_str(welcome_json)
            .map_err(|e| MarmotError::Internal(format!("Invalid welcome JSON: {}", e)))?;

        let event_id = EventId::from_hex(&input.wrapper_event_id)
            .map_err(|e| MarmotError::Internal(format!("Invalid event ID: {}", e)))?;
        let rumor: UnsignedEvent = serde_json::from_value(input.rumor_event)
            .map_err(|e| MarmotError::Internal(format!("Invalid rumor event: {}", e)))?;

        // Process the welcome
        let welcome = mdk
            .process_welcome(&event_id, &rumor)
            .map_err(|e| MarmotError::Internal(format!("Failed to process welcome: {}", e)))?;

        // Accept the welcome
        mdk.accept_welcome(&welcome)
            .map_err(|e| MarmotError::Internal(format!("Failed to accept welcome: {}", e)))?;

        // Get group info
        let group_id = welcome.mls_group_id.as_slice().to_vec();
        let group_name = welcome.group_name.clone();
        let epoch = 0u64; // Will be updated after processing

        // Get members
        let members = mdk
            .get_members(&welcome.mls_group_id)
            .map_err(|e| MarmotError::Internal(format!("Failed to get members: {}", e)))?;
        let member_pubkeys: Vec<String> = members.iter().map(|pk| pk.to_hex()).collect();

        Ok((group_id, group_name, epoch, member_pubkeys))
    }

    /// Encrypt a message for a group.
    /// Returns JSON-serialized Nostr event.
    pub fn encrypt_message(&self, group_id: &[u8], plaintext: &str) -> Result<Vec<u8>, MarmotError> {
        let mdk = self.mdk.write();
        let mls_group_id = mdk_core::GroupId::from_slice(group_id);

        // Create an unsigned event (rumor) with the message content
        let rumor = UnsignedEvent::new(
            self.keys.public_key(),
            nostr::Timestamp::now(),
            nostr::Kind::Custom(9), // Kind 9 for chat messages
            vec![],
            plaintext.to_string(),
        );

        // Create the encrypted message
        let event = mdk
            .create_message(&mls_group_id, rumor)
            .map_err(|e| MarmotError::Internal(format!("Failed to encrypt message: {}", e)))?;

        // Serialize to JSON
        let event_json = serde_json::to_vec(&event)
            .map_err(|e| MarmotError::SerializationError(format!("Failed to serialize event: {}", e)))?;

        Ok(event_json)
    }

    /// Decrypt a message from a group.
    /// ciphertext: JSON-serialized Nostr event
    /// Returns (sender_pubkey, plaintext, epoch).
    pub fn decrypt_message(&self, _group_id: &[u8], ciphertext: &[u8]) -> Result<(String, String, u64), MarmotError> {
        let mdk = self.mdk.write();

        // Parse the event from JSON
        let event_json = std::str::from_utf8(ciphertext)
            .map_err(|e| MarmotError::Internal(format!("Invalid UTF-8: {}", e)))?;
        let event: Event = serde_json::from_str(event_json)
            .map_err(|e| MarmotError::Internal(format!("Invalid event JSON: {}", e)))?;

        // Process the message
        let result = mdk
            .process_message(&event)
            .map_err(|e| MarmotError::Internal(format!("Failed to process message: {}", e)))?;

        // Extract the message content based on result type
        match result {
            mdk_core::messages::MessageProcessingResult::ApplicationMessage(msg) => {
                let sender = msg.pubkey.to_hex();
                let content = msg.content.clone();
                let epoch = 0u64; // TODO: Get actual epoch
                Ok((sender, content, epoch))
            }
            _ => Err(MarmotError::Internal("Unexpected message type".into())),
        }
    }

    /// Process a commit message.
    pub fn process_commit(&self, _group_id: &[u8], commit_data: &[u8]) -> Result<(), MarmotError> {
        let mdk = self.mdk.write();

        // Parse the event from JSON
        let event_json = std::str::from_utf8(commit_data)
            .map_err(|e| MarmotError::Internal(format!("Invalid UTF-8: {}", e)))?;
        let event: Event = serde_json::from_str(event_json)
            .map_err(|e| MarmotError::Internal(format!("Invalid event JSON: {}", e)))?;

        // Process as a message (commits are processed the same way)
        mdk.process_message(&event)
            .map_err(|e| MarmotError::Internal(format!("Failed to process commit: {}", e)))?;

        Ok(())
    }

    /// Update keys for forward secrecy.
    /// Returns JSON-serialized commit event.
    pub fn update_keys(&self, group_id: &[u8]) -> Result<Vec<u8>, MarmotError> {
        let mdk = self.mdk.write();
        let mls_group_id = mdk_core::GroupId::from_slice(group_id);

        // Perform self-update
        let result = mdk
            .self_update(&mls_group_id)
            .map_err(|e| MarmotError::Internal(format!("Failed to update keys: {}", e)))?;

        // Merge the pending commit
        mdk.merge_pending_commit(&mls_group_id)
            .map_err(|e| MarmotError::Internal(format!("Failed to merge commit: {}", e)))?;

        // Serialize the evolution event
        let event_json = serde_json::to_vec(&result.evolution_event)
            .map_err(|e| MarmotError::SerializationError(format!("Failed to serialize event: {}", e)))?;

        Ok(event_json)
    }

    /// Remove a member from a group.
    /// Returns JSON-serialized commit event.
    pub fn remove_member(&self, group_id: &[u8], member_public_key: &str) -> Result<Vec<u8>, MarmotError> {
        let mdk = self.mdk.write();
        let mls_group_id = mdk_core::GroupId::from_slice(group_id);

        // Parse the member's public key
        let pubkey = PublicKey::from_hex(member_public_key)
            .map_err(|e| MarmotError::InvalidKey(format!("Invalid public key: {}", e)))?;

        // Remove the member
        let result = mdk
            .remove_members(&mls_group_id, &[pubkey])
            .map_err(|e| MarmotError::Internal(format!("Failed to remove member: {}", e)))?;

        // Merge the pending commit
        mdk.merge_pending_commit(&mls_group_id)
            .map_err(|e| MarmotError::Internal(format!("Failed to merge commit: {}", e)))?;

        // Serialize the evolution event
        let event_json = serde_json::to_vec(&result.evolution_event)
            .map_err(|e| MarmotError::SerializationError(format!("Failed to serialize event: {}", e)))?;

        Ok(event_json)
    }

    /// Get information about a group.
    /// Returns (name, epoch, members_json) or None if not found.
    pub fn get_group_info(&self, group_id: &[u8]) -> Option<(String, u64, Vec<String>)> {
        let mdk = self.mdk.read();
        let mls_group_id = mdk_core::GroupId::from_slice(group_id);

        // Get the group
        let group = mdk.get_group(&mls_group_id).ok()??;

        // Get members
        let members = mdk.get_members(&mls_group_id).ok()?;
        let member_pubkeys: Vec<String> = members.iter().map(|pk| pk.to_hex()).collect();

        Some((
            group.name.clone(),
            0, // TODO: Get actual epoch
            member_pubkeys,
        ))
    }

    /// Export group state for persistence.
    /// Note: With memory storage, this exports the current state but
    /// the state will be lost on restart.
    pub fn export_group_state(&self, group_id: &[u8]) -> Result<Vec<u8>, MarmotError> {
        let mdk = self.mdk.read();
        let mls_group_id = mdk_core::GroupId::from_slice(group_id);

        // Get the group and serialize it
        let group = mdk
            .get_group(&mls_group_id)
            .map_err(|e| MarmotError::Internal(format!("Failed to get group: {}", e)))?
            .ok_or_else(|| MarmotError::GroupNotFound(hex::encode(group_id)))?;

        let state = serde_json::to_vec(&group)
            .map_err(|e| MarmotError::SerializationError(format!("Failed to serialize group: {}", e)))?;

        Ok(state)
    }

    /// Import group state from persistence.
    /// Note: With memory storage, imported state is not automatically restored.
    pub fn import_group_state(&self, _group_id: &[u8], _state: &[u8]) -> Result<(), MarmotError> {
        // With memory storage, we can't easily import state
        // This would require the storage to support import
        Err(MarmotError::Internal("Import not supported with memory storage".into()))
    }
}
