# Deployment Guide

## Overview

This guide covers deploying the Shipment Tracking Service to AWS using AWS SAM (Serverless Application Model). The deployment process includes setting up shared resources (DynamoDB), deploying both Lambda functions, and configuring secrets.

## Prerequisites

### Required Tools

- **AWS CLI** (v2.x): [Installation Guide](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html)
- **AWS SAM CLI** (v1.x): [Installation Guide](https://docs.aws.amazon.com/serverless-application-model/latest/developerguide/install-sam-cli.html)
- **.NET 8 SDK** (8.0.416+): [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Docker** (optional, for SAM local testing): [Installation](https://docs.docker.com/get-docker/)

### AWS Account Setup

1. **Create AWS Account** or use existing account
2. **Configure AWS CLI**:
   ```bash
   aws configure
   # Enter your AWS Access Key ID
   # Enter your AWS Secret Access Key
   # Enter your default region (e.g., us-east-1)
   # Enter your default output format (json)
   ```

3. **Verify AWS Configuration**:
   ```bash
   aws sts get-caller-identity
   ```

### Required AWS Permissions

Your IAM user/role needs permissions to create:
- Lambda functions
- DynamoDB tables
- SNS topics
- SQS queues
- API Gateway APIs
- CloudWatch log groups and alarms
- IAM roles and policies
- Secrets Manager secrets
- S3 buckets (for SAM deployment artifacts)

## Deployment Architecture

```
Environments: dev → uat → prod

Each environment has:
├── Shared Resources (DynamoDB, Secrets)
├── Subscriber Lambda + SNS + DLQ
└── Webhook Processor Lambda + API Gateway + SNS
```

## Deployment Steps

### Step 1: Build the Solution

```bash
# Navigate to repository root
cd /path/to/ShipmentProcessing

# Restore dependencies
dotnet restore ShipmentTracking.sln

# Build in Release mode
dotnet build ShipmentTracking.sln --configuration Release

# Run tests (if available)
dotnet test ShipmentTracking.sln --configuration Release
```

### Step 2: Deploy Shared Resources (One-Time)

The shared resources include DynamoDB table and can be shared across environments or deployed per-environment.

**Option A: Single Shared Table Across Environments**
```bash
cd infrastructure/shared

sam deploy \
  --template-file dynamodb-tables.yaml \
  --stack-name shipment-tracking-shared \
  --parameter-overrides \
    Environment=shared \
    TableName=ShipmentTrackingSubscriptions \
    EnablePointInTimeRecovery=true \
  --capabilities CAPABILITY_IAM \
  --region us-east-1
```

**Option B: Per-Environment Table**
```bash
cd infrastructure/shared

# For dev environment
sam deploy \
  --template-file dynamodb-tables.yaml \
  --stack-name shipment-tracking-shared-dev \
  --parameter-overrides \
    Environment=dev \
    TableName=ShipmentTrackingSubscriptions-dev \
    EnablePointInTimeRecovery=false \
  --capabilities CAPABILITY_IAM \
  --region us-east-1

# Repeat for uat and prod with appropriate parameters
```

### Step 3: Configure Secrets

Store carrier API credentials in AWS Secrets Manager:

```bash
# USPS Credentials
aws secretsmanager create-secret \
  --name carrier/usps/credentials \
  --description "USPS API credentials for shipment tracking" \
  --secret-string '{
    "apiKey": "YOUR_USPS_API_KEY",
    "clientId": "YOUR_USPS_CLIENT_ID",
    "clientSecret": "YOUR_USPS_CLIENT_SECRET"
  }' \
  --region us-east-1

# USPS Webhook Secret
aws secretsmanager create-secret \
  --name carrier/usps/webhook-secret \
  --description "USPS webhook signature validation secret" \
  --secret-string "YOUR_USPS_WEBHOOK_SECRET" \
  --region us-east-1

# Verify secrets were created
aws secretsmanager list-secrets --region us-east-1
```

**For multiple environments**, use environment-specific secret names:
```bash
# Dev environment
aws secretsmanager create-secret \
  --name carrier/usps/credentials-dev \
  --secret-string '{...}'

# Prod environment
aws secretsmanager create-secret \
  --name carrier/usps/credentials-prod \
  --secret-string '{...}'
```

### Step 4: Deploy Subscriber Lambda

```bash
cd infrastructure/subscriber-lambda

# Build the Lambda function
sam build --use-container

# Deploy to dev environment
sam deploy \
  --stack-name shipment-tracking-subscriber-dev \
  --parameter-overrides \
    Environment=dev \
    DynamoDbTableName=ShipmentTrackingSubscriptions-dev \
    WebhookBaseUrl=https://webhook-dev.example.com \
    UspsApiBaseUrl=https://api.usps.com \
    LogRetentionDays=7 \
  --capabilities CAPABILITY_IAM \
  --resolve-s3 \
  --region us-east-1

# Confirm deployment
aws lambda get-function \
  --function-name shipment-tracking-subscriber-dev \
  --region us-east-1
```

**Deployment Options**:

**Interactive Guided Deploy** (first time):
```bash
sam deploy --guided
```
This will prompt you for all parameters and save them to `samconfig.toml`.

**Subsequent Deploys**:
```bash
sam deploy  # Uses saved configuration
```

**Deploy to UAT**:
```bash
sam deploy \
  --stack-name shipment-tracking-subscriber-uat \
  --parameter-overrides \
    Environment=uat \
    DynamoDbTableName=ShipmentTrackingSubscriptions-uat \
    WebhookBaseUrl=https://webhook-uat.example.com \
    LogRetentionDays=30 \
  --capabilities CAPABILITY_IAM \
  --resolve-s3
```

**Deploy to Production**:
```bash
sam deploy \
  --stack-name shipment-tracking-subscriber-prod \
  --parameter-overrides \
    Environment=prod \
    DynamoDbTableName=ShipmentTrackingSubscriptions-prod \
    WebhookBaseUrl=https://webhook.example.com \
    LogRetentionDays=90 \
  --capabilities CAPABILITY_IAM \
  --resolve-s3 \
  --no-confirm-changeset  # Remove to review changes first
```

### Step 5: Deploy Webhook Processor Lambda

First, you need to obtain an ACM certificate for your custom domain (if using):

```bash
# Request certificate (or use existing)
aws acm request-certificate \
  --domain-name webhook.example.com \
  --validation-method DNS \
  --region us-east-1

# Note the CertificateArn from output
```

Deploy the Webhook Processor:

```bash
cd infrastructure/webhook-processor-lambda

# Build the Lambda function
sam build --use-container

# Deploy to dev environment
sam deploy \
  --stack-name shipment-tracking-webhook-processor-dev \
  --parameter-overrides \
    Environment=dev \
    DynamoDbTableName=ShipmentTrackingSubscriptions-dev \
    CustomDomainName=webhook-dev.example.com \
    CertificateArn=arn:aws:acm:us-east-1:123456789012:certificate/abc123 \
    LogRetentionDays=7 \
  --capabilities CAPABILITY_IAM \
  --resolve-s3 \
  --region us-east-1

# Get API Gateway endpoint URL
aws cloudformation describe-stacks \
  --stack-name shipment-tracking-webhook-processor-dev \
  --query 'Stacks[0].Outputs[?OutputKey==`WebhookApiUrl`].OutputValue' \
  --output text \
  --region us-east-1
```

### Step 6: Configure DNS (Custom Domain)

If using a custom domain, create a DNS record pointing to API Gateway:

1. **Get API Gateway domain name**:
   ```bash
   aws apigateway get-domain-names \
     --region us-east-1 \
     --query 'items[?domainName==`webhook-dev.example.com`].regionalDomainName' \
     --output text
   ```

2. **Create CNAME record** in your DNS provider:
   ```
   webhook-dev.example.com → d-1234567890.execute-api.us-east-1.amazonaws.com
   ```

3. **Verify DNS resolution**:
   ```bash
   dig webhook-dev.example.com
   nslookup webhook-dev.example.com
   ```

### Step 7: Test Deployment

**Test Subscriber Lambda**:
```bash
# Create test SNS event
cat > test-event.json << 'EOF'
{
  "Records": [{
    "Sns": {
      "Message": "{\"fulfillmentOrderId\":\"FO-12345\",\"orderId\":\"ORD-67890\",\"shippedAt\":\"2026-01-09T12:00:00Z\",\"packages\":[{\"packageId\":\"PKG-001\",\"carrier\":\"USPS\",\"trackingNumber\":\"9400116901490039382136\",\"serviceLevel\":\"Priority\"}]}"
    }
  }]
}
EOF

# Invoke Lambda directly
aws lambda invoke \
  --function-name shipment-tracking-subscriber-dev \
  --payload file://test-event.json \
  --region us-east-1 \
  response.json

cat response.json
```

**Test Webhook Processor Lambda**:
```bash
# Test via API Gateway
curl -X POST https://webhook-dev.example.com/webhook/usps \
  -H "Content-Type: application/json" \
  -H "X-USPS-Signature: <valid-signature>" \
  -H "X-USPS-Timestamp: $(date +%s)" \
  -d '{
    "trackingNumber": "9400116901490039382136",
    "status": "Out for Delivery",
    "statusCode": "04",
    "eventTimestamp": "2026-01-10T09:30:00Z"
  }'
```

### Step 8: Monitor Deployment

**Check Lambda Logs**:
```bash
# Subscriber Lambda logs
aws logs tail /aws/lambda/shipment-tracking-subscriber-dev --follow --region us-east-1

# Webhook Processor Lambda logs
aws logs tail /aws/lambda/shipment-tracking-webhook-processor-dev --follow --region us-east-1
```

**Check CloudWatch Alarms**:
```bash
aws cloudwatch describe-alarms \
  --alarm-name-prefix "shipment-tracking" \
  --region us-east-1
```

**View CloudWatch Dashboard**:
```bash
# Open in browser
echo "https://console.aws.amazon.com/cloudwatch/home?region=us-east-1#dashboards:name=shipment-tracking-subscriber-dev"
```

## Multi-Environment Strategy

### Environment Configuration

Create parameter files for each environment:

**infrastructure/subscriber-lambda/parameters-dev.json**:
```json
{
  "Parameters": {
    "Environment": "dev",
    "DynamoDbTableName": "ShipmentTrackingSubscriptions-dev",
    "WebhookBaseUrl": "https://webhook-dev.example.com",
    "UspsApiBaseUrl": "https://api.usps.com",
    "LogRetentionDays": "7"
  }
}
```

**infrastructure/subscriber-lambda/parameters-prod.json**:
```json
{
  "Parameters": {
    "Environment": "prod",
    "DynamoDbTableName": "ShipmentTrackingSubscriptions-prod",
    "WebhookBaseUrl": "https://webhook.example.com",
    "UspsApiBaseUrl": "https://api.usps.com",
    "LogRetentionDays": "90"
  }
}
```

**Deploy with parameter file**:
```bash
sam deploy \
  --stack-name shipment-tracking-subscriber-prod \
  --parameter-overrides file://parameters-prod.json \
  --capabilities CAPABILITY_IAM \
  --resolve-s3
```

### Promotion Process

```
Dev (automatic on merge to main)
  ↓ (manual approval)
UAT (manual trigger)
  ↓ (manual approval + smoke tests)
Production (manual trigger)
```

**GitHub Actions Workflow** handles this automatically (see `.github/workflows/deploy-subscriber.yml`).

## CI/CD Integration

### GitHub Actions

The repository includes GitHub Actions workflows for automated deployment:

**Required Secrets** (set in GitHub repository settings):
- `AWS_ACCESS_KEY_ID_DEV`
- `AWS_SECRET_ACCESS_KEY_DEV`
- `AWS_ACCESS_KEY_ID_UAT`
- `AWS_SECRET_ACCESS_KEY_UAT`
- `AWS_ACCESS_KEY_ID_PROD`
- `AWS_SECRET_ACCESS_KEY_PROD`

**Workflow Triggers**:
- Push to `main` → Deploy to dev
- Manual workflow dispatch → Deploy to uat/prod

**Workflow Steps**:
1. Checkout code
2. Setup .NET 8
3. Build solution
4. Run tests
5. CodeQL security scan
6. SAM build
7. SAM deploy
8. Smoke tests
9. Create release (prod only)

### Manual Deployment

If not using CI/CD:

```bash
# 1. Build
cd /path/to/ShipmentProcessing
dotnet build ShipmentTracking.sln --configuration Release

# 2. Package
cd infrastructure/subscriber-lambda
sam build --use-container

# 3. Deploy
sam deploy \
  --stack-name shipment-tracking-subscriber-prod \
  --parameter-overrides Environment=prod \
  --capabilities CAPABILITY_IAM \
  --resolve-s3

# 4. Verify
aws lambda get-function --function-name shipment-tracking-subscriber-prod
```

## Rollback Procedures

### CloudFormation Rollback

CloudFormation automatically rolls back failed deployments:

```bash
# Check stack status
aws cloudformation describe-stacks \
  --stack-name shipment-tracking-subscriber-prod \
  --query 'Stacks[0].StackStatus' \
  --region us-east-1

# View events during rollback
aws cloudformation describe-stack-events \
  --stack-name shipment-tracking-subscriber-prod \
  --region us-east-1
```

### Manual Rollback

Rollback to previous Lambda version:

```bash
# List versions
aws lambda list-versions-by-function \
  --function-name shipment-tracking-subscriber-prod \
  --region us-east-1

# Update alias to previous version
aws lambda update-alias \
  --function-name shipment-tracking-subscriber-prod \
  --name live \
  --function-version 5 \  # Previous version
  --region us-east-1
```

### Stack Rollback

Rollback entire stack to previous state:

```bash
aws cloudformation continue-update-rollback \
  --stack-name shipment-tracking-subscriber-prod \
  --region us-east-1
```

## Troubleshooting

### Common Deployment Issues

**Issue**: `Unable to find AWS credentials`
```bash
# Solution: Configure AWS CLI
aws configure
# Or set environment variables
export AWS_ACCESS_KEY_ID=your-key
export AWS_SECRET_ACCESS_KEY=your-secret
export AWS_DEFAULT_REGION=us-east-1
```

**Issue**: `Stack already exists`
```bash
# Solution: Update existing stack
sam deploy --stack-name <existing-stack-name>
# Or delete and recreate
aws cloudformation delete-stack --stack-name <stack-name>
```

**Issue**: `Insufficient permissions`
```bash
# Solution: Verify IAM permissions
aws iam get-user
aws iam list-attached-user-policies --user-name <your-user>
```

**Issue**: `Lambda function code size too large`
```bash
# Solution: Optimize build
cd src/ShipmentTracking.Subscriber
dotnet publish -c Release -r linux-x64 --self-contained false
# Use Lambda layers for shared dependencies
```

**Issue**: `SAM build fails`
```bash
# Solution: Clean and rebuild
sam build --use-container --debug
# Or build locally without container
dotnet restore
dotnet build --configuration Release
sam build
```

### Monitoring Deployment Health

**Check Lambda Metrics**:
```bash
aws cloudwatch get-metric-statistics \
  --namespace AWS/Lambda \
  --metric-name Invocations \
  --dimensions Name=FunctionName,Value=shipment-tracking-subscriber-prod \
  --start-time 2026-01-09T00:00:00Z \
  --end-time 2026-01-09T23:59:59Z \
  --period 3600 \
  --statistics Sum \
  --region us-east-1
```

**Check API Gateway Metrics**:
```bash
aws cloudwatch get-metric-statistics \
  --namespace AWS/ApiGateway \
  --metric-name Count \
  --dimensions Name=ApiName,Value=shipment-tracking-webhook-api-prod \
  --start-time 2026-01-09T00:00:00Z \
  --end-time 2026-01-09T23:59:59Z \
  --period 3600 \
  --statistics Sum \
  --region us-east-1
```

## Post-Deployment Tasks

### 1. Configure Monitoring

Set up SNS topics for alarm notifications:
```bash
aws sns create-topic --name shipment-tracking-alarms --region us-east-1
aws sns subscribe \
  --topic-arn arn:aws:sns:us-east-1:123456789012:shipment-tracking-alarms \
  --protocol email \
  --notification-endpoint ops-team@example.com
```

### 2. Enable X-Ray Tracing

```bash
aws lambda update-function-configuration \
  --function-name shipment-tracking-subscriber-prod \
  --tracing-config Mode=Active \
  --region us-east-1
```

### 3. Configure Auto Scaling

For DynamoDB tables (if using provisioned capacity):
```bash
aws application-autoscaling register-scalable-target \
  --service-namespace dynamodb \
  --resource-id table/ShipmentTrackingSubscriptions-prod \
  --scalable-dimension dynamodb:table:ReadCapacityUnits \
  --min-capacity 5 \
  --max-capacity 100
```

### 4. Set Up Backup Plan

```bash
aws backup create-backup-plan \
  --backup-plan file://backup-plan.json \
  --region us-east-1
```

## Cost Optimization

### Lambda

- **Right-size memory**: Test with different memory allocations
- **Use ARM architecture**: Consider switching to arm64 for cost savings
- **Monitor cold starts**: Use provisioned concurrency if needed

### DynamoDB

- **On-demand vs Provisioned**: Use on-demand for variable workloads
- **Enable TTL**: Automatically delete old records
- **Use point-in-time recovery**: Only in production

### API Gateway

- **Caching**: Enable response caching for frequently accessed data
- **Throttling**: Set appropriate throttling limits

## Security Checklist

- [ ] Enable CloudTrail logging
- [ ] Configure VPC endpoints (optional)
- [ ] Set up AWS WAF for API Gateway
- [ ] Enable encryption at rest and in transit
- [ ] Rotate secrets regularly
- [ ] Review IAM policies (least privilege)
- [ ] Enable MFA for AWS console access
- [ ] Set up security alerts
- [ ] Perform security audit

## Related Documentation

- [Architecture Overview](../README.md)
- [Subscriber Lambda Architecture](../subscriber-lambda/ARCHITECTURE.md)
- [Webhook Processor Architecture](../webhook-processor-lambda/ARCHITECTURE.md)
- [Monitoring Guide](./MONITORING.md)
- [CI/CD Pipeline](./CICD_PIPELINE.md)
