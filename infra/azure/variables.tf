variable "subscription_id" {
  type        = string
  description = "Azure subscription in which to provision the PoC."
}

variable "location" {
  type        = string
  description = "Azure region supporting Container Apps Consumption and Cosmos DB serverless."
}

variable "prefix" {
  type        = string
  description = "Globally unique lowercase alphanumeric prefix, used in Azure resource names."

  validation {
    condition     = can(regex("^[a-z][a-z0-9]{4,11}$", var.prefix))
    error_message = "Use 5-12 lowercase letters/digits starting with a letter."
  }
}

variable "applications" {
  description = "Null bootstraps infrastructure only. Supply both apps to deploy images already pushed to ACR."
  type = object({
    helpdesk = object({
      image_digest = string
      authority    = string
      audience     = string
    })
    agentdesk = object({
      image_digest = string
      authority    = string
      audience     = string
    })
  })
  default = null

  validation {
    condition = var.applications == null ? true : alltrue([
      for app in [var.applications.helpdesk, var.applications.agentdesk] :
      can(regex("^sha256:[0-9a-f]{64}$", app.image_digest)) &&
      can(regex("^https://[^/?#[:space:]]+(/[^?#[:space:]]*)?$", app.authority)) &&
      trimspace(app.audience) != ""
    ])
    error_message = "Each app needs a SHA-256 image digest, an HTTPS Entra authority, and a non-empty API audience."
  }
}

variable "helpdesk_min_replicas" {
  type        = number
  description = "Set to 1 to keep Helpdesk background jobs warm. AgentDesk always has one replica."
  default     = 0

  validation {
    condition     = contains([0, 1], var.helpdesk_min_replicas)
    error_message = "Helpdesk must use 0 or 1 minimum replicas."
  }
}

variable "secret_writer_principal_id" {
  type        = string
  description = "Entra object ID of the operator or pipeline that seeds Key Vault. Not an application/client ID."
}

variable "budget" {
  description = "Optional monthly Azure cost alert. Amount is in the subscription billing currency; not a spending cap."
  type = object({
    amount     = number
    start_date = string
    email      = string
  })
  default = null
}
