locals {
  app_hosts = {
    for key, name in local.app_names : key => "${name}.${azurerm_container_app_environment.poc.default_domain}"
  }

  common_env = {
    ASPNETCORE_ENVIRONMENT = "Production"
  }

  app_env = {
    helpdesk = {
      Queue__Provider                         = "StorageQueue"
      StorageQueue__QueueServiceUri           = azurerm_storage_account.poc.primary_queue_endpoint
      StorageQueue__QueueName                 = azurerm_storage_queue.intake["private-corp-tickets"].name
      Attachments__Provider                   = "Azure"
      Attachments__ServiceUri                 = azurerm_storage_account.poc.primary_blob_endpoint
      Attachments__Container                  = azurerm_storage_container.attachments.name
      Attachments__CreateContainerIfNotExists = "false"
      Conversation__ActiveKeyId               = "poc"
      Http__HttpsRedirection                  = "false"
    }
    agentdesk = {
      Auth__Enforce                     = "true"
      Storage__Provider                 = "Cosmos"
      Cosmos__Endpoint                  = azurerm_cosmosdb_account.poc.endpoint
      Cosmos__Database                  = azurerm_cosmosdb_sql_database.agentdesk.name
      Cosmos__CreateIfNotExists         = "false"
      Cosmos__EnableVectorPolicy        = "false"
      Blobs__Provider                   = "Azure"
      Blobs__ServiceUri                 = azurerm_storage_account.poc.primary_blob_endpoint
      Blobs__Container                  = azurerm_storage_container.attachments.name
      Blobs__CreateContainerIfNotExists = "false"
      Intake__Provider                  = "StorageQueue"
      Intake__QueueServiceUri           = azurerm_storage_account.poc.primary_queue_endpoint
      Intake__QueueName                 = azurerm_storage_queue.intake["private-corp-tickets"].name
    }
  }

  helpdesk_secrets = {
    "anthropic-api-key"        = "Anthropic__ApiKey"
    "conversation-signing-key" = "Conversation__Keys__poc"
  }
}

resource "azurerm_container_app" "app" {
  for_each                     = var.applications == null ? {} : tomap(var.applications)
  name                         = local.app_names[each.key]
  resource_group_name          = azurerm_resource_group.poc.name
  container_app_environment_id = azurerm_container_app_environment.poc.id
  workload_profile_name        = "Consumption"
  revision_mode                = "Single"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app[each.key].id]
  }

  registry {
    server   = azurerm_container_registry.poc.login_server
    identity = azurerm_user_assigned_identity.app[each.key].id
  }

  dynamic "secret" {
    for_each = each.key == "helpdesk" ? local.helpdesk_secrets : {}
    content {
      name                = secret.key
      identity            = azurerm_user_assigned_identity.app[each.key].id
      key_vault_secret_id = "${trimsuffix(azurerm_key_vault.poc.vault_uri, "/")}/secrets/${secret.key}"
    }
  }

  ingress {
    external_enabled           = true
    allow_insecure_connections = false
    target_port                = 8080
    transport                  = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = each.key == "helpdesk" ? var.helpdesk_min_replicas : 1
    max_replicas = 1

    container {
      name   = each.key
      image  = "${azurerm_container_registry.poc.login_server}/${each.key}@${each.value.image_digest}"
      cpu    = 0.5
      memory = "1Gi"

      dynamic "env" {
        for_each = merge(local.common_env, local.app_env[each.key], {
          AZURE_CLIENT_ID = azurerm_user_assigned_identity.app[each.key].client_id
          AllowedHosts    = local.app_hosts[each.key]
          Auth__Authority = each.value.authority
          Auth__Audience  = each.value.audience
        })
        content {
          name  = env.key
          value = env.value
        }
      }

      dynamic "env" {
        for_each = each.key == "helpdesk" ? local.helpdesk_secrets : {}
        content {
          name        = env.value
          secret_name = env.key
        }
      }

      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/healthz"
        interval_seconds        = 5
        failure_count_threshold = 24
        header {
          name  = "Host"
          value = local.app_hosts[each.key]
        }
      }

      liveness_probe {
        transport        = "HTTP"
        port             = 8080
        path             = "/healthz"
        interval_seconds = 30
        timeout          = 3
        header {
          name  = "Host"
          value = local.app_hosts[each.key]
        }
      }

      readiness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/readyz"
        interval_seconds        = 30
        timeout                 = 7
        success_count_threshold = 1
        header {
          name  = "Host"
          value = local.app_hosts[each.key]
        }
      }
    }
  }

  # References are versionless; rotate the values in Key Vault, not in Terraform.
  lifecycle {
    ignore_changes = [secret]
  }

  depends_on = [
    azurerm_role_assignment.pull,
    azurerm_role_assignment.read_helpdesk_secrets,
    azurerm_role_assignment.attachments,
    azurerm_role_assignment.delegate_blob_sas,
    azurerm_role_assignment.helpdesk_queue,
    azurerm_role_assignment.agentdesk_queues,
    azurerm_cosmosdb_sql_container.agentdesk,
    azurerm_cosmosdb_sql_role_assignment.agentdesk,
  ]
}
