mock_provider "azurerm" {
  mock_data "azurerm_client_config" {
    defaults = {
      tenant_id = "00000000-0000-0000-0000-000000000003"
    }
  }

  mock_resource "azurerm_container_app_environment" {
    defaults = {
      default_domain = "example.azurecontainerapps.io"
    }
  }
}

variables {
  subscription_id            = "00000000-0000-0000-0000-000000000001"
  location                   = "uksouth"
  prefix                     = "testpoc01"
  secret_writer_principal_id = "00000000-0000-0000-0000-000000000002"
}

run "foundation_without_images" {
  command = plan

  assert {
    condition     = length(azurerm_container_app.app) == 0 && length(azurerm_storage_queue.intake) == 2
    error_message = "The first stage must create both queues without starting either app."
  }

  assert {
    condition     = azurerm_cosmosdb_sql_container.agentdesk["tickets"].default_ttl == -1 && !azurerm_storage_account.poc.shared_access_key_enabled
    error_message = "Ticket receipt TTL and identity-only Storage access must be retained."
  }
}

run "apps_with_images" {
  command = plan

  variables {
    applications = {
      helpdesk = {
        image_digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        authority    = "https://login.example.com/tenant/v2.0"
        audience     = "helpdesk-api"
      }
      agentdesk = {
        image_digest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        authority    = "https://login.example.com/tenant/v2.0"
        audience     = "agentdesk-api"
      }
    }
  }

  assert {
    condition = alltrue([
      for key, app in azurerm_container_app.app :
      app.revision_mode == "Single" && app.template[0].max_replicas == 1 &&
      app.template[0].min_replicas == (key == "agentdesk" ? 1 : 0) &&
      !app.ingress[0].allow_insecure_connections
    ])
    error_message = "Both apps must use HTTPS and single-replica limits; the consumer must stay warm."
  }

  assert {
    condition = anytrue([
      for setting in azurerm_container_app.app["agentdesk"].template[0].container[0].env :
      setting.name == "Auth__Enforce" && setting.value == "true"
    ])
    error_message = "AgentDesk must not start in anonymous mode."
  }
}
