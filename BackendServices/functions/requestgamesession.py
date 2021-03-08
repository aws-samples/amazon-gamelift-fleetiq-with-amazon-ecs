# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: MIT-0

import json
import datetime
import time
import os
import boto3
from datetime import timedelta
import random

# Tries to find an existing or free game session and return the IP and Port to the client

def lambda_handler(event, context):

    sqs_client = boto3.client('sqs')

    # 1. Check SQS Queue if there are sessions available
    # Try to receive message from SQS queue
    try:
        response = sqs_client.receive_message(
            QueueUrl=os.environ['SQS_QUEUE_URL'],
            MaxNumberOfMessages=1,
            VisibilityTimeout=15,
            WaitTimeSeconds=1
        )
        message = response['Messages'][0]
        print(message)
        receipt_handle = message['ReceiptHandle']
        connection_info = message['Body']
        print(receipt_handle)
        print("got session: " + connection_info)

        connection_splitted = connection_info.split(":")
        ip = connection_splitted[0]
        port = connection_splitted[1]

        print("IP: " + ip + " PORT: " + port)

        # Delete received message from queue
        sqs_client.delete_message(
            QueueUrl=os.environ['SQS_QUEUE_URL'],
            ReceiptHandle=receipt_handle
        )

        # Return result to client
        return {
            "statusCode": 200,
            "body": json.dumps({ 'publicIP': ip, 'port': port })
        }
    except:
        print("Failed getting a session from the SQS queue, will try claiming a new one")

    # 2. If not, try to claim a new session through FleetIQ
    client = boto3.client('gamelift')
    response = client.claim_game_server(
        GameServerGroupName='ExampleGameServerGroup',
    )
    print(response)
    connection_info = response["GameServer"]["ConnectionInfo"]
    try:
        connection_splitted = connection_info.split(":")
        ip = connection_splitted[0]
        port = connection_splitted[1]

        print("IP: " + ip + " PORT: " + port)

        # Put a ticket in to the SQS for the next player (we match 1-v-1 sessions)
        response = sqs_client.send_message(
            QueueUrl=os.environ['SQS_QUEUE_URL'],
            MessageBody=(
                connection_info
            )
        )
        print(response['MessageId'])

        return {
            "statusCode": 200,
            "body": json.dumps({ 'publicIP': ip, 'port': port })
        }
    except:
        print("Failed getting a new session")

    # 3. Failed to find a server
    return {
            "statusCode": 500,
            "body": json.dumps({ 'failed': 'couldnt find a free server spot'})
    }