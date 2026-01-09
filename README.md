# ShipmentProcessing

## Shipment Tracking Service

A cloud-native, event-driven system for real-time shipment tracking across multiple carriers (USPS, UPS, FedEx). Built with AWS Lambda (.NET 8), this service subscribes to carrier tracking webhooks and processes status updates to provide a unified tracking experience.

## 🚀 Features

- **Multi-Carrier Support**: USPS (Day 1), with extensible architecture for UPS and FedEx
- **Real-Time Updates**: Event-driven architecture with carrier webhook subscriptions
- **Idempotent Processing**: Prevents duplicate subscriptions with DynamoDB conditional writes
- **Secure**: HMAC-SHA256 webhook signature validation, AWS Secrets Manager integration
- **Scalable**: Serverless architecture that scales automatically with demand
- **Observable**: Structured logging, custom metrics, and distributed tracing
- **Resilient**: Circuit breaker pattern, exponential backoff, and comprehensive error handling

## 🏗️ Architecture

The system consists of two AWS Lambda functions:

### 1. Subscriber Lambda
Processes fulfillment order shipped events and subscribes to carrier tracking webhooks.

**Trigger**: SNS Topic (`fulfillment-order-shipped`)  
**Output**: Subscription records in DynamoDB

### 2. Webhook Processor Lambda
Receives carrier webhook callbacks and publishes canonical tracking status events.

**Trigger**: API Gateway (`POST /webhook/{carrier}`)  
**Output**: SNS Topic (`shipment-tracking-status-events`)

```
Order System → SNS → Subscriber Lambda → Carrier APIs
                                           ↓ (webhooks)
Downstream ← SNS ← Webhook Processor ← API Gateway
```

## 📋 Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [AWS CLI](https://aws.amazon.com/cli/) configured with appropriate credentials
- [SAM CLI](https://docs.aws.amazon.com/serverless-application-model/latest/developerguide/serverless-sam-cli-install.html) (for deployment)
- AWS Account with appropriate permissions

## 🛠️ Quick Start

### 1. Clone the Repository

```bash
git clone https://github.com/hsavaliya702/ShipmentProcessing.git
cd ShipmentProcessing
```

### 2. Build the Solution

```bash
dotnet build ShipmentTracking.sln
```

### 3. Run Tests

```bash
dotnet test ShipmentTracking.sln
```

### 4. Deploy to AWS

#### Deploy DynamoDB Table (one-time)

```bash
cd infrastructure/shared
sam deploy \
  --template-file dynamodb-tables.yaml \
  --stack-name shipment-tracking-dynamodb-dev \
  --parameter-overrides Environment=dev \
  --capabilities CAPABILITY_IAM
```

#### Deploy Subscriber Lambda

```bash
cd infrastructure/subscriber-lambda
sam build
sam deploy \
  --stack-name shipment-tracking-subscriber-dev \
  --parameter-overrides Environment=dev \
  --capabilities CAPABILITY_IAM \
  --resolve-s3
```

#### Deploy Webhook Processor Lambda

```bash
cd infrastructure/webhook-processor-lambda
sam build
sam deploy \
  --stack-name shipment-tracking-webhook-processor-dev \
  --parameter-overrides Environment=dev \
  --capabilities CAPABILITY_IAM \
  --resolve-s3
```

## 📖 Documentation

- [Architecture Overview](./docs/README.md) - System design and component interactions
- [Subscriber Lambda Documentation](./docs/subscriber-lambda/) - Detailed implementation guide
- [Webhook Processor Documentation](./docs/webhook-processor-lambda/) - Webhook processing pipeline
- [Deployment Guide](./docs/deployment/) - Infrastructure and CI/CD setup
- [Contributing Guide](./CONTRIBUTING.md) - Development guidelines

## 🔧 Configuration

### Environment Variables

#### Subscriber Lambda
- `DYNAMODB_TABLE_NAME`: DynamoDB table name for subscription records
- `WEBHOOK_BASE_URL`: Base URL for carrier webhook callbacks
- `USPS_API_BASE_URL`: USPS API endpoint

#### Webhook Processor Lambda
- `DYNAMODB_TABLE_NAME`: DynamoDB table name for correlation data
- `SNS_TOPIC_ARN`: SNS topic ARN for publishing canonical events

### AWS Secrets

Store carrier credentials in AWS Secrets Manager:

```bash
# USPS Credentials
aws secretsmanager create-secret \
  --name carrier/usps/credentials \
  --secret-string '{"apiKey":"YOUR_API_KEY","clientId":"YOUR_CLIENT_ID","clientSecret":"YOUR_CLIENT_SECRET"}'

# USPS Webhook Secret
aws secretsmanager create-secret \
  --name carrier/usps/webhook-secret \
  --secret-string "YOUR_WEBHOOK_SECRET"
```

## 🧪 Testing

### Unit Tests

```bash
dotnet test src/ShipmentTracking.Common.Tests/
dotnet test src/ShipmentTracking.Subscriber.Tests/
dotnet test src/ShipmentTracking.WebhookProcessor.Tests/
```

### Integration Tests

```bash
# Requires LocalStack or AWS credentials
dotnet test tests/ShipmentTracking.IntegrationTests/
```

### Local Development

Use the AWS Lambda Mock Test Tool:

```bash
cd src/ShipmentTracking.Subscriber
dotnet lambda-test-tool
```

## 📊 Monitoring

### CloudWatch Dashboards

Each Lambda has a CloudWatch dashboard showing:
- Invocation count and error rate
- Duration and concurrent executions
- API Gateway metrics (Webhook Processor)
- Custom business metrics

### Alarms

Configured alarms for:
- High error rates
- DLQ message count
- Long execution times
- API 4xx/5xx errors
- DynamoDB throttling

## 🔒 Security

- **Webhook Validation**: HMAC-SHA256 signature verification
- **IAM Least Privilege**: Minimal permissions for each Lambda
- **Secrets Management**: All credentials in AWS Secrets Manager
- **Encryption**: Data encrypted at rest (DynamoDB, SNS) and in transit (HTTPS)
- **VPC**: Optional VPC deployment for enhanced network isolation

## 📈 Performance

- **Subscriber Lambda**: 100-300ms per package (warm)
- **Webhook Processor**: 50-150ms per webhook (warm)
- **Throughput**: 100s-1000s concurrent executions
- **Cost**: ~$85-160/month for 1M shipments

## 🤝 Contributing

We welcome contributions! Please see [CONTRIBUTING.md](./CONTRIBUTING.md) for:
- Development setup
- Code standards and conventions
- Pull request process
- Testing requirements

## 📝 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🆘 Support

- **Issues**: [GitHub Issues](https://github.com/hsavaliya702/ShipmentProcessing/issues)
- **Discussions**: [GitHub Discussions](https://github.com/hsavaliya702/ShipmentProcessing/discussions)
- **Email**: support@example.com

## 🗺️ Roadmap

- [ ] UPS integration
- [ ] FedEx integration
- [ ] Advanced analytics dashboard
- [ ] Predictive delivery ETAs with ML
- [ ] Multi-region deployment
- [ ] GraphQL API for querying tracking history

## 📚 Technology Stack

- **.NET 8**: Lambda runtime
- **AWS Lambda**: Serverless compute
- **API Gateway**: RESTful webhook endpoint
- **DynamoDB**: NoSQL database for subscriptions
- **SNS**: Event publishing and messaging
- **Secrets Manager**: Credential storage
- **CloudWatch**: Logging and monitoring
- **SAM/CloudFormation**: Infrastructure as Code
- **Polly**: Resilience and transient fault handling

## ⭐ Acknowledgments

- AWS Lambda .NET SDK Team
- Polly resilience library
- Open source community

---

**Built with ❤️ using AWS Serverless and .NET 8**

