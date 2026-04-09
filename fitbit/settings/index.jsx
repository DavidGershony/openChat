function OpenChatSettings(props) {
  const token = props.settingsStorage.getItem("bridge_token");
  const isPaired = token && token.length > 0;

  return (
    <Page>
      <Section title={<Text bold align="center">OpenChat Watch</Text>}>

        {!isPaired ? (
          <Section>
            <Text>
              Pair with the OpenChat app on your phone to receive messages
              on your watch.
            </Text>
            <Text bold>Steps:</Text>
            <Text>1. Open OpenChat on your phone</Text>
            <Text>2. Go to Settings → Smartwatch → Pair Watch</Text>
            <Text>3. Enter the 6-digit code below</Text>

            <TextInput
              label="Pairing Code"
              settingsKey="pairing_code_input"
              type="number"
              placeholder="Enter 6-digit code"
            />

            <Button
              label="Connect"
              onClick={() => {
                const codeInput = props.settingsStorage.getItem("pairing_code_input");
                if (!codeInput) return;

                let code;
                try {
                  // settingsStorage wraps values in JSON
                  code = JSON.parse(codeInput).name || codeInput;
                } catch (e) {
                  code = codeInput;
                }

                code = code.trim();
                if (code.length !== 6) {
                  props.settingsStorage.setItem("pairing_status", "Code must be 6 digits");
                  return;
                }

                props.settingsStorage.setItem("pairing_status", "Connecting...");

                fetch("http://127.0.0.1:18457/api/v1/auth/pair", {
                  method: "POST",
                  headers: { "Content-Type": "application/json" },
                  body: JSON.stringify({ code })
                })
                  .then(resp => {
                    if (!resp.ok) {
                      throw new Error(resp.status === 403 ? "Invalid or expired code" : `Error ${resp.status}`);
                    }
                    return resp.json();
                  })
                  .then(data => {
                    props.settingsStorage.setItem("bridge_token", data.token);
                    props.settingsStorage.setItem("pairing_status", "Connected!");
                    props.settingsStorage.removeItem("pairing_code_input");
                  })
                  .catch(err => {
                    props.settingsStorage.setItem("pairing_status", `Failed: ${err.message}`);
                  });
              }}
            />

            <Text italic>
              {props.settingsStorage.getItem("pairing_status") || ""}
            </Text>
          </Section>
        ) : (
          <Section>
            <Text bold>Connected to OpenChat</Text>
            <Text>
              Your watch is paired and receiving messages.
            </Text>

            <Button
              label="Disconnect"
              onClick={() => {
                props.settingsStorage.removeItem("bridge_token");
                props.settingsStorage.setItem("pairing_status", "Disconnected");
              }}
            />
          </Section>
        )}
      </Section>
    </Page>
  );
}

registerSettingsPage(OpenChatSettings);
