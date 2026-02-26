//! MLS Group implementation.

use rand::rngs::OsRng;
use rand::RngCore;
use serde::{Deserialize, Serialize};

use crate::client::WelcomeData;
use crate::error::MarmotError;

/// Represents an MLS group with its state.
#[derive(Debug, Serialize, Deserialize)]
pub struct MlsGroup {
    /// Unique group identifier
    pub group_id: Vec<u8>,
    /// Human-readable group name
    pub name: String,
    /// Current epoch (increments on each state change)
    pub epoch: u64,
    /// List of member public keys
    pub members: Vec<String>,
    /// Group secret (simplified - in production would be proper MLS tree secrets)
    group_secret: Vec<u8>,
    /// Application secret for encrypting messages
    application_secret: Vec<u8>,
}

impl MlsGroup {
    /// Create a new MLS group.
    pub fn create(name: &str, creator_public_key: &str) -> Result<Self, MarmotError> {
        let mut group_id = vec![0u8; 32];
        OsRng.fill_bytes(&mut group_id);

        let mut group_secret = vec![0u8; 32];
        OsRng.fill_bytes(&mut group_secret);

        let application_secret = derive_application_secret(&group_secret, 0);

        Ok(Self {
            group_id,
            name: name.to_string(),
            epoch: 0,
            members: vec![creator_public_key.to_string()],
            group_secret,
            application_secret,
        })
    }

    /// Create a group from a Welcome message.
    pub fn from_welcome(welcome: &WelcomeData, joiner_public_key: &str) -> Result<Self, MarmotError> {
        // In production, this would decrypt the Welcome and verify everything
        // For now, we trust the Welcome data

        if !welcome.members.contains(&joiner_public_key.to_string()) {
            return Err(MarmotError::NotMember);
        }

        let application_secret = derive_application_secret(&welcome.group_secrets, welcome.epoch);

        Ok(Self {
            group_id: welcome.group_id.clone(),
            name: welcome.group_name.clone(),
            epoch: welcome.epoch,
            members: welcome.members.clone(),
            group_secret: welcome.group_secrets.clone(),
            application_secret,
        })
    }

    /// Add a member and return a Welcome message.
    pub fn add_member(&mut self, member_public_key: &str) -> Result<Vec<u8>, MarmotError> {
        if self.members.contains(&member_public_key.to_string()) {
            return Err(MarmotError::AlreadyMember);
        }

        // Add member
        self.members.push(member_public_key.to_string());

        // Update epoch
        self.epoch += 1;

        // Derive new secrets
        self.group_secret = derive_next_secret(&self.group_secret);
        self.application_secret = derive_application_secret(&self.group_secret, self.epoch);

        // Create Welcome message
        let welcome = WelcomeData {
            group_id: self.group_id.clone(),
            group_name: self.name.clone(),
            epoch: self.epoch,
            members: self.members.clone(),
            group_secrets: self.group_secret.clone(),
            encrypted_group_info: vec![], // Simplified
        };

        let welcome_data = serde_json::to_vec(&welcome)?;

        Ok(welcome_data)
    }

    /// Remove a member and return a commit.
    pub fn remove_member(&mut self, member_public_key: &str) -> Result<Vec<u8>, MarmotError> {
        let pos = self
            .members
            .iter()
            .position(|m| m == member_public_key)
            .ok_or(MarmotError::MemberNotFound(member_public_key.to_string()))?;

        self.members.remove(pos);

        // Update epoch
        self.epoch += 1;

        // Derive new secrets (removed member won't have these)
        self.group_secret = derive_next_secret(&self.group_secret);
        self.application_secret = derive_application_secret(&self.group_secret, self.epoch);

        // Create commit message
        let commit = CommitData {
            group_id: self.group_id.clone(),
            epoch: self.epoch,
            removed_members: vec![member_public_key.to_string()],
            added_members: vec![],
        };

        let commit_data = serde_json::to_vec(&commit)?;

        Ok(commit_data)
    }

    /// Update keys (self-update for forward secrecy).
    pub fn update_keys(&mut self) -> Result<Vec<u8>, MarmotError> {
        // Update epoch
        self.epoch += 1;

        // Derive new secrets
        self.group_secret = derive_next_secret(&self.group_secret);
        self.application_secret = derive_application_secret(&self.group_secret, self.epoch);

        // Create commit message
        let commit = CommitData {
            group_id: self.group_id.clone(),
            epoch: self.epoch,
            removed_members: vec![],
            added_members: vec![],
        };

        let commit_data = serde_json::to_vec(&commit)?;

        Ok(commit_data)
    }

    /// Process a commit message.
    pub fn process_commit(&mut self, commit_data: &[u8]) -> Result<(), MarmotError> {
        let commit: CommitData = serde_json::from_slice(commit_data)?;

        if commit.group_id != self.group_id {
            return Err(MarmotError::InvalidState("Group ID mismatch".into()));
        }

        if commit.epoch != self.epoch + 1 {
            return Err(MarmotError::InvalidState(format!(
                "Unexpected epoch: expected {}, got {}",
                self.epoch + 1,
                commit.epoch
            )));
        }

        // Apply changes
        for member in &commit.removed_members {
            self.members.retain(|m| m != member);
        }
        for member in &commit.added_members {
            if !self.members.contains(member) {
                self.members.push(member.clone());
            }
        }

        // Update epoch and secrets
        self.epoch = commit.epoch;
        self.group_secret = derive_next_secret(&self.group_secret);
        self.application_secret = derive_application_secret(&self.group_secret, self.epoch);

        Ok(())
    }

    /// Encrypt a message for the group.
    pub fn encrypt_message(&mut self, plaintext: &str, sender_public_key: &str) -> Result<Vec<u8>, MarmotError> {
        if !self.members.contains(&sender_public_key.to_string()) {
            return Err(MarmotError::NotMember);
        }

        // Simple XOR encryption (NOT SECURE - for demo only)
        // In production, use proper AEAD encryption with the application secret
        let plaintext_bytes = plaintext.as_bytes();
        let mut ciphertext = Vec::with_capacity(plaintext_bytes.len());

        for (i, &byte) in plaintext_bytes.iter().enumerate() {
            ciphertext.push(byte ^ self.application_secret[i % self.application_secret.len()]);
        }

        // Create encrypted message envelope
        let message = EncryptedMessage {
            sender_public_key: sender_public_key.to_string(),
            epoch: self.epoch,
            ciphertext,
        };

        let message_data = serde_json::to_vec(&message)?;

        Ok(message_data)
    }

    /// Decrypt a message from the group.
    pub fn decrypt_message(&mut self, ciphertext_data: &[u8]) -> Result<(String, String, u64), MarmotError> {
        let message: EncryptedMessage = serde_json::from_slice(ciphertext_data)?;

        if !self.members.contains(&message.sender_public_key) {
            return Err(MarmotError::NotMember);
        }

        // Simple XOR decryption (NOT SECURE - for demo only)
        let mut plaintext_bytes = Vec::with_capacity(message.ciphertext.len());

        for (i, &byte) in message.ciphertext.iter().enumerate() {
            plaintext_bytes.push(byte ^ self.application_secret[i % self.application_secret.len()]);
        }

        let plaintext = String::from_utf8(plaintext_bytes)?;

        Ok((message.sender_public_key, plaintext, message.epoch))
    }

    /// Export group state for persistence.
    pub fn export_state(&self) -> Result<Vec<u8>, MarmotError> {
        let state = serde_json::to_vec(self)?;
        Ok(state)
    }

    /// Import group state from persistence.
    pub fn import_state(state: &[u8]) -> Result<Self, MarmotError> {
        let group: Self = serde_json::from_slice(state)?;
        Ok(group)
    }
}

/// Commit message data structure.
#[derive(Debug, Serialize, Deserialize)]
struct CommitData {
    group_id: Vec<u8>,
    epoch: u64,
    removed_members: Vec<String>,
    added_members: Vec<String>,
}

/// Encrypted message envelope.
#[derive(Debug, Serialize, Deserialize)]
struct EncryptedMessage {
    sender_public_key: String,
    epoch: u64,
    ciphertext: Vec<u8>,
}

/// Derive the next group secret (simplified key schedule).
fn derive_next_secret(current_secret: &[u8]) -> Vec<u8> {
    use sha2::{Digest, Sha256};

    let mut hasher = Sha256::new();
    hasher.update(current_secret);
    hasher.update(b"mls_next_secret");
    hasher.finalize().to_vec()
}

/// Derive the application secret from the group secret and epoch.
fn derive_application_secret(group_secret: &[u8], epoch: u64) -> Vec<u8> {
    use sha2::{Digest, Sha256};

    let mut hasher = Sha256::new();
    hasher.update(group_secret);
    hasher.update(b"mls_application_secret");
    hasher.update(&epoch.to_le_bytes());
    hasher.finalize().to_vec()
}
