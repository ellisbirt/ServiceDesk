# Azure proof-of-concept deployment

[`infra/azure`](../infra/azure) provisions a Consumption Container Apps environment, Basic ACR, managed identities, Key Vault, Standard LRS Blob Storage and intake queues, single-region serverless Cosmos DB, and Log Analytics. Apply the foundation first, then publish images and deploy the apps. The state backend and Entra registrations are prerequisites, not resources managed by this stack.

This is a low-traffic PoC, not a highly available or private production deployment. Check regional availability and pricing before applying. AgentDesk stays warm to consume the queue; Helpdesk scales to zero by default, so expect cold starts and do not rely on background work surviving idle scale-in. Both apps cap at one replica. In a single-revision rollout, Azure can temporarily overlap old and new replicas: use a maintenance window and expect active jobs and SignalR connections to be interrupted. ACR, storage, log ingestion, Cosmos requests and model calls can incur charges even when Helpdesk scales to zero.

## 1. Apply the foundation

Use Terraform 1.10+, Azure CLI and Docker. Use an existing private Azure Storage container for Terraform state, **outside** the PoC resource group. Grant the Terraform operator `Storage Blob Data Contributor` on that state container and permission to create the PoC resources and role assignments in the target subscription. Keep state access restricted; providers can store sensitive resource metadata even though this stack does not manage application secret values.

```sh
az login
az account set --subscription "<subscription-id>"
cd infra/azure
cp state.tfbackend.example state.tfbackend
cp poc.tfvars.example poc.tfvars
# Edit both files. Leave applications unset for the first apply.
terraform init -backend-config=state.tfbackend
terraform plan -var-file=poc.tfvars -out=foundation.tfplan
terraform apply foundation.tfplan
terraform output -json app_urls
```

Use the output origins as the SPA redirect URIs. Configure each API registration's delegated scope and expected audience; Helpdesk requires the `tenant_id` token claim, while AgentDesk also accepts `tid` and uses its application roles. Restrict pilot access in Entra, assign the appropriate user roles, and verify the claims in actual access tokens before opening the pilot. The app URLs are public HTTPS; this stack does not provision private networking.

## 2. Seed Key Vault and publish images

Using the designated secret writer identity, create `anthropic-api-key` and `conversation-signing-key` in the vault named by `terraform output -raw key_vault_name`. Use the Azure portal or a secret-aware pipeline. The signing key must be a stable base64-encoded random value of at least 32 bytes. Wait for RBAC propagation if access is initially denied. Do not put these values in `.tfvars`, Docker build arguments, or the repository.

From the repository root, build and push images with the **public** Entra SPA settings. Use a unique tag per release; Terraform deploys the digest, not the tag. The publisher needs `AcrPush` (or equivalent) on the registry. Container Apps pull through their managed identities; the registry must allow ARM-audience tokens (`az acr config authentication-as-arm show --registry "$ACR"`).

```sh
ACR=$(terraform -chdir=infra/azure output -raw registry_name)
SERVER=$(terraform -chdir=infra/azure output -raw registry_server)
TAG="<unique-release-tag>"
az acr login --name "$ACR"

docker build --platform linux/amd64 -f helpdesk/Dockerfile -t "$SERVER/helpdesk:$TAG" \
  --build-arg VITE_ENTRA_CLIENT_ID="<helpdesk-spa-client-id>" \
  --build-arg VITE_ENTRA_AUTHORITY="https://<authority>" \
  --build-arg VITE_API_SCOPE="<helpdesk-api-scope>" .
docker build --platform linux/amd64 -f agentdesk/Dockerfile -t "$SERVER/agentdesk:$TAG" \
  --build-arg VITE_ENTRA_CLIENT_ID="<agentdesk-spa-client-id>" \
  --build-arg VITE_ENTRA_AUTHORITY="https://<authority>" \
  --build-arg VITE_API_SCOPE="<agentdesk-api-scope>" .
docker push "$SERVER/helpdesk:$TAG"
docker push "$SERVER/agentdesk:$TAG"
az acr repository show --name "$ACR" --image "helpdesk:$TAG" --query digest -o tsv
az acr repository show --name "$ACR" --image "agentdesk:$TAG" --query digest -o tsv
```

## 3. Deploy and operate

Fill in `applications` in `poc.tfvars` with both digests and runtime Entra settings, then re-plan and apply:

```sh
cd infra/azure
terraform plan -var-file=poc.tfvars -out=apps.tfplan
terraform apply apps.tfplan
terraform output -json app_urls
```

Check `/healthz` and `/readyz` on both origins, then sign in and verify a confirmed Helpdesk hand-off appears in AgentDesk. Test browser upload and download against the shared blob container. AgentDesk uses its offline assist/embedding defaults; vector search is disabled. HTTPS terminates at Container Apps ingress; Helpdesk's in-container redirect is disabled to avoid a redirect loop.

Container Apps console and system logs feed Log Analytics (`ContainerAppConsoleLogs_CL` and `ContainerAppSystemLogs_CL`), with 30-day retention and a configured daily ingestion quota. Reaching the quota can interrupt log collection. Full OTLP traces/metrics, dashboards and on-call alerting are not provisioned. The optional `budget` emails at 80% of monthly spend; it does not stop resources or cover external model charges.

Keep the `applications` block on subsequent applies; removing it plans to delete both apps. To roll back, set the previous known-good digests and apply during a maintenance window. Keep those digests in ACR. To tear down the PoC, review a destroy plan with the same variables first: Terraform will remove the application's persistent ticket and attachment storage. The external state backend and Entra registrations are not removed.
