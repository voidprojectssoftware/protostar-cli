Feature: Sessions back up in the background while I work
  As a developer
  I want the session hooks to hand the upload to a background process
  So that my harness is never kept waiting for a backup

  Scenario: The Stop hook returns while its background push is still waiting on the registry
    Given I am signed in to a registry that accepts connections but never answers
    And a session transcript on disk
    When the Stop hook fires for the transcript
    Then the hook exits 0 within 10 seconds
    And a background push is connected to the registry after the hook has exited
    When the registry drops the connection
    Then the background push logs "the registry could not be reached"
