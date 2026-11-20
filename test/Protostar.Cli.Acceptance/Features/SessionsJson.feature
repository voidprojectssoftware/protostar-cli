Feature: Session commands give agents structured results
  As a coding agent running protostar for a developer
  I want session results as JSON records with stable ids, and errors with codes
  So that I can act on them without parsing tables meant for people

  Scenario: sessions status --json prints one record per pending transcript
    Given a session transcript "6f83525b-790f-4654-944d-ba3b0c96453e" in the harness
    When I run protostar with "sessions status --json --harness-home {harnessHome} --registry https://127.0.0.1:1"
    Then the exit code is 1
    And standard output is a JSON array whose records have ids "6f83525b-790f-4654-944d-ba3b0c96453e"
    And the output contains "neverBackedUp"

  Scenario: Not signed in, sessions push --json gives an error code and nothing on standard output
    Given a session transcript "6f83525b-790f-4654-944d-ba3b0c96453e" in the harness
    When I run protostar with "sessions push --json --harness-home {harnessHome} --registry https://not-signed-in.invalid"
    Then the exit code is 1
    And standard output is empty
    And standard error is a JSON error with code "not_signed_in"

  Scenario: Not signed in, sessions list --json still lists this machine's pending sessions and says why the rest are missing
    Given a session transcript "6f83525b-790f-4654-944d-ba3b0c96453e" in the harness
    When I run protostar with "sessions list --json --harness-home {harnessHome} --registry https://not-signed-in.invalid"
    Then the exit code is 1
    And standard output is a JSON array whose records have ids "6f83525b-790f-4654-944d-ba3b0c96453e"
    And standard error is a JSON error with code "not_signed_in"

  Scenario: An unusable registry URL is an error code too
    When I run protostar with "sessions status --json --harness-home {harnessHome} --registry not-a-url"
    Then the exit code is 1
    And standard output is empty
    And standard error is a JSON error with code "invalid_registry"
